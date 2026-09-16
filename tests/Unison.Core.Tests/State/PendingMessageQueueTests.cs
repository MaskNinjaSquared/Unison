// =============================================================================
// Tests for PendingMessageQueue.
//
// This is the buffer between "a message arrived" and "it is on disk", so the
// failure mode is messages that quietly never get written. It was seven fields
// under a lock inside WhatsAppService, reachable only by running the app; these
// exist so that it is reachable by running dotnet test.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public class PendingMessageQueueTests
    {
        private const int Threshold = 12;
        private const int BatchCap = 1500;
        private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(750);
        private static readonly DateTime T0 = new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);

        private static PendingMessageQueue NewQueue(int threshold = Threshold, int cap = BatchCap) =>
            new PendingMessageQueue(threshold, Interval, cap);

        private static ChatMessage Msg(string? id, int minute = 0, bool fromMe = false) =>
            new ChatMessage { Id = id, Timestamp = T0.AddMinutes(minute), IsFromMe = fromMe };

        private static ChatMessage[] Many(int count) =>
            Enumerable.Range(0, count).Select(i => Msg("m" + i, i)).ToArray();

        // --- queueing --------------------------------------------------------

        [Fact]
        public void Queued_messages_are_counted()
        {
            var queue = NewQueue();

            queue.Add("a@s.whatsapp.net", new[] { Msg("1"), Msg("2") }, T0);

            Assert.Equal(2, queue.PendingCount);
        }

        [Fact]
        public void Requeueing_the_same_id_replaces_rather_than_duplicates()
        {
            var queue = NewQueue();
            queue.Add("a@s.whatsapp.net", new[] { Msg("1", minute: 0) }, T0);

            queue.Add("a@s.whatsapp.net", new[] { Msg("1", minute: 5) }, T0);

            Assert.Equal(1, queue.PendingCount);
            var drained = queue.Drain(T0)!.MessagesByChat["a@s.whatsapp.net"];
            Assert.Equal(T0.AddMinutes(5), Assert.Single(drained).Timestamp);
        }

        [Fact]
        public void Messages_without_an_id_are_all_kept()
        {
            // Nothing to deduplicate against, so none may be dropped.
            var queue = NewQueue();

            queue.Add("a@s.whatsapp.net", new[] { Msg(null), Msg(""), Msg("  ") }, T0);

            Assert.Equal(3, queue.PendingCount);
        }

        [Fact]
        public void Nulls_and_empty_batches_are_ignored()
        {
            var queue = NewQueue();

            Assert.Equal(PendingFlushAction.None, queue.Add(null, new[] { Msg("1") }, T0));
            Assert.Equal(PendingFlushAction.None, queue.Add("a@s.whatsapp.net", null, T0));
            Assert.Equal(PendingFlushAction.None, queue.Add("a@s.whatsapp.net", new ChatMessage?[] { null }, T0));
            Assert.Equal(0, queue.PendingCount);
        }

        [Fact]
        public void Chats_are_kept_apart()
        {
            var queue = NewQueue();

            queue.Add("a@s.whatsapp.net", new[] { Msg("1") }, T0);
            queue.Add("b@s.whatsapp.net", new[] { Msg("2") }, T0);

            var drain = queue.Drain(T0)!;
            Assert.Equal(2, drain.ChatCount);
        }

        // --- when to flush ---------------------------------------------------

        [Fact]
        public void A_small_batch_only_arms_the_timer()
        {
            var queue = NewQueue();

            Assert.Equal(PendingFlushAction.ScheduleTimer, queue.Add("a@s.whatsapp.net", Many(3), T0));
        }

        [Fact]
        public void Reaching_the_message_threshold_calls_for_a_flush()
        {
            var queue = NewQueue();

            Assert.Equal(PendingFlushAction.FlushNow, queue.Add("a@s.whatsapp.net", Many(Threshold), T0));
        }

        [Fact]
        public void The_interval_since_the_first_message_also_calls_for_a_flush()
        {
            var queue = NewQueue();
            queue.Add("a@s.whatsapp.net", new[] { Msg("1") }, T0);

            var action = queue.Add("a@s.whatsapp.net", new[] { Msg("2") }, T0 + Interval);

            Assert.Equal(PendingFlushAction.FlushNow, action);
        }

        [Fact]
        public void The_interval_clock_starts_at_the_first_message_not_at_construction()
        {
            // Otherwise a session that sat idle would flush on its very first message.
            var queue = NewQueue();

            var action = queue.Add("a@s.whatsapp.net", new[] { Msg("1") }, T0.AddHours(3));

            Assert.Equal(PendingFlushAction.ScheduleTimer, action);
        }

        [Fact]
        public void Only_one_caller_is_handed_the_flush()
        {
            var queue = NewQueue();
            Assert.Equal(PendingFlushAction.FlushNow, queue.Add("a@s.whatsapp.net", Many(Threshold), T0));

            var second = queue.Add("b@s.whatsapp.net", Many(Threshold), T0);

            Assert.Equal(PendingFlushAction.ScheduleTimer, second);
        }

        [Fact]
        public void A_caller_already_inside_a_flush_cycle_asks_for_nothing()
        {
            var queue = NewQueue();

            var action = queue.Add("a@s.whatsapp.net", Many(Threshold), T0, scheduleFlush: false);

            Assert.Equal(PendingFlushAction.None, action);
            Assert.Equal(Threshold, queue.PendingCount);
        }

        // --- TryClaimFlush ---------------------------------------------------

        [Fact]
        public void The_timer_cannot_claim_a_flush_with_nothing_to_write()
        {
            Assert.False(NewQueue().TryClaimFlush());
        }

        [Fact]
        public void The_timer_claims_a_flush_when_work_is_waiting()
        {
            var queue = NewQueue();
            queue.Add("a@s.whatsapp.net", new[] { Msg("1") }, T0);

            Assert.True(queue.TryClaimFlush());
            Assert.False(queue.TryClaimFlush());
        }

        // --- draining --------------------------------------------------------

        [Fact]
        public void Draining_an_empty_queue_says_so_rather_than_handing_back_nothing_to_do()
        {
            Assert.Null(NewQueue().Drain(T0));
        }

        [Fact]
        public void Draining_empties_the_queue()
        {
            var queue = NewQueue();
            queue.Add("a@s.whatsapp.net", new[] { Msg("1") }, T0);

            var drain = queue.Drain(T0)!;

            Assert.Equal(0, queue.PendingCount);
            Assert.Single(drain.MessagesByChat["a@s.whatsapp.net"]);
        }

        [Fact]
        public void Draining_carries_the_dirty_chats_with_it()
        {
            var queue = NewQueue();
            queue.Add("a@s.whatsapp.net", new[] { Msg("1") }, T0);
            queue.MarkDirty("b@s.whatsapp.net");

            var drain = queue.Drain(T0)!;

            Assert.Contains("a@s.whatsapp.net", drain.DirtyChats);
            Assert.Contains("b@s.whatsapp.net", drain.DirtyChats);
        }

        [Fact]
        public void A_dirty_chat_on_its_own_is_not_work_to_flush()
        {
            // Recorded: the count tracks messages, so marking a chat dirty without
            // queueing one does not by itself cause a write.
            var queue = NewQueue();

            queue.MarkDirty("a@s.whatsapp.net");

            Assert.Null(queue.Drain(T0));
        }

        [Fact]
        public void Draining_restarts_the_interval_clock()
        {
            var queue = NewQueue();
            queue.Add("a@s.whatsapp.net", new[] { Msg("1") }, T0);
            queue.Drain(T0.AddMinutes(10));

            var action = queue.Add("a@s.whatsapp.net", new[] { Msg("2") }, T0.AddMinutes(10));

            Assert.Equal(PendingFlushAction.ScheduleTimer, action);
        }

        // --- failure and retry -----------------------------------------------

        [Fact]
        public void A_failed_write_is_put_back()
        {
            var queue = NewQueue();
            queue.Add("a@s.whatsapp.net", new[] { Msg("1"), Msg("2") }, T0);
            var drain = queue.Drain(T0)!;

            queue.Restore(drain);

            Assert.Equal(2, queue.PendingCount);
            Assert.Equal(2, queue.Drain(T0)!.MessagesByChat["a@s.whatsapp.net"].Count);
        }

        [Fact]
        public void Restoring_null_is_harmless()
        {
            var queue = NewQueue();

            queue.Restore(null);

            Assert.Equal(0, queue.PendingCount);
        }

        [Fact]
        public void A_restored_message_overwrites_a_newer_one_queued_meanwhile()
        {
            // Recorded, not endorsed: carried over from the original requeue. The newer
            // copy loses, but the message itself is not lost - it stays queued and the
            // next flush writes the older content.
            var queue = NewQueue();
            queue.Add("a@s.whatsapp.net", new[] { Msg("1", minute: 0) }, T0);
            var drain = queue.Drain(T0)!;
            queue.Add("a@s.whatsapp.net", new[] { Msg("1", minute: 9) }, T0);

            queue.Restore(drain);

            var pending = queue.Drain(T0)!.MessagesByChat["a@s.whatsapp.net"];
            Assert.Equal(T0, Assert.Single(pending).Timestamp);
        }

        [Fact]
        public void Completing_a_flush_reports_whether_more_arrived_meanwhile()
        {
            var queue = NewQueue();
            queue.Add("a@s.whatsapp.net", Many(Threshold), T0);
            queue.Drain(T0);

            Assert.False(queue.CompleteFlush());

            queue.Add("a@s.whatsapp.net", new[] { Msg("late") }, T0);
            Assert.True(queue.CompleteFlush());
        }

        [Fact]
        public void Completing_a_flush_releases_the_claim()
        {
            var queue = NewQueue();
            queue.Add("a@s.whatsapp.net", Many(Threshold), T0);
            Assert.True(queue.IsFlushClaimed);

            queue.CompleteFlush();

            Assert.False(queue.IsFlushClaimed);
        }

        // --- SelectBatch -----------------------------------------------------

        [Fact]
        public void The_batch_keeps_the_latest_version_of_each_message()
        {
            var queue = NewQueue();

            var batch = queue.SelectBatch(new[] { Msg("1", minute: 0), Msg("1", minute: 5) });

            Assert.Equal(T0.AddMinutes(5), Assert.Single(batch).Timestamp);
        }

        [Fact]
        public void The_batch_comes_back_oldest_first()
        {
            var queue = NewQueue();

            var batch = queue.SelectBatch(new[] { Msg("b", minute: 5), Msg("a", minute: 1) });

            Assert.Equal(new[] { "a", "b" }, batch.Select(m => m.Id));
        }

        [Fact]
        public void The_batch_is_capped_at_the_newest_messages()
        {
            var queue = NewQueue(cap: 2);

            var batch = queue.SelectBatch(new[] { Msg("a", 1), Msg("b", 2), Msg("c", 3) });

            Assert.Equal(new[] { "b", "c" }, batch.Select(m => m.Id));
        }

        [Fact]
        public void The_batch_tolerates_nothing_to_do()
        {
            var queue = NewQueue();

            Assert.Empty(queue.SelectBatch(null));
            Assert.Empty(queue.SelectBatch(new ChatMessage?[] { null }));
        }

        // --- SnapshotFor -----------------------------------------------------

        [Fact]
        public void A_chat_can_be_read_back_under_any_address_that_resolves_to_it()
        {
            var queue = NewQueue();
            queue.Add("400500600@lid", new[] { Msg("1") }, T0);
            queue.Add("5511888888888@s.whatsapp.net", new[] { Msg("2") }, T0);

            // Both addresses are the same person.
            var snapshot = queue.SnapshotFor("400500600@lid", _ => "same");

            Assert.Equal(2, snapshot.Count);
        }

        [Fact]
        public void An_unrelated_chat_is_not_read_back()
        {
            var queue = NewQueue();
            queue.Add("a@s.whatsapp.net", new[] { Msg("1") }, T0);

            var snapshot = queue.SnapshotFor("b@s.whatsapp.net", jid => jid);

            Assert.Empty(snapshot);
        }

        [Fact]
        public void Reading_back_does_not_consume()
        {
            var queue = NewQueue();
            queue.Add("a@s.whatsapp.net", new[] { Msg("1") }, T0);

            queue.SnapshotFor("a@s.whatsapp.net", jid => jid);

            Assert.Equal(1, queue.PendingCount);
        }
    }
}
