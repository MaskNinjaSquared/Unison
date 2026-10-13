using System;
using System.Threading.Tasks;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public sealed class IncomingMessageQueueTests
    {
        private sealed class Item
        {
            public Item(string id, bool offline)
            {
                Id = id;
                Offline = offline;
            }

            public string Id { get; }
            public bool Offline { get; }
        }

        private static readonly DateTime Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static IncomingMessageQueue<Item> NewQueue() =>
            new IncomingMessageQueue<Item>(item => item.Offline, item => item.Id);

        [Fact]
        public void Live_items_are_served_before_offline_ones()
        {
            var queue = NewQueue();
            queue.Enqueue(new Item("off-1", true));
            queue.Enqueue(new Item("live-1", false));
            queue.Enqueue(new Item("off-2", true));
            int generation;
            queue.TryStart(_ => Task.CompletedTask, Now, out generation);

            Item next;
            Assert.True(queue.TryTakeNext(generation, Now, out next));
            Assert.Equal("live-1", next.Id);
            Assert.True(queue.TryTakeNext(generation, Now, out next));
            Assert.Equal("off-1", next.Id);
            Assert.True(queue.TryTakeNext(generation, Now, out next));
            Assert.Equal("off-2", next.Id);
            Assert.False(queue.TryTakeNext(generation, Now, out next));
            Assert.Equal("idle", queue.Stage);
            Assert.False(queue.Snapshot().Running);
        }

        [Fact]
        public void A_second_start_does_nothing_while_one_is_running_and_an_empty_queue_never_starts()
        {
            var queue = NewQueue();
            int starts = 0;
            int generation;

            Assert.False(queue.TryStart(_ => { starts++; return Task.CompletedTask; }, Now, out generation));
            queue.Enqueue(new Item("a", false));
            Assert.True(queue.TryStart(_ => { starts++; return Task.CompletedTask; }, Now, out generation));
            Assert.False(queue.TryStart(_ => { starts++; return Task.CompletedTask; }, Now, out generation));

            Assert.Equal(1, starts);
            Assert.Equal("starting", queue.Stage);
        }

        [Fact]
        public void The_pump_task_is_the_one_the_start_returned()
        {
            var queue = NewQueue();
            queue.Enqueue(new Item("a", false));
            var pending = new TaskCompletionSource<bool>();
            int generation;

            queue.TryStart(_ => pending.Task, Now, out generation);

            Assert.Same(pending.Task, queue.PumpTask);
        }

        [Fact]
        public void Reset_retires_the_drain_and_puts_the_held_item_back_in_its_lane()
        {
            var queue = NewQueue();
            var held = new Item("held", true);
            queue.Enqueue(held);
            queue.Enqueue(new Item("live", false));
            int generation;
            queue.TryStart(_ => Task.CompletedTask, Now, out generation);
            Item next;
            queue.TryTakeNext(generation, Now, out next);
            queue.TryTakeNext(generation, Now, out next);
            Assert.Same(held, next);

            IncomingQueueReset reset = queue.Reset("health-stall", requeueCurrent: true, Now);

            Assert.Equal(generation + 1, reset.Generation);
            Assert.Equal(0, reset.LiveDepth);
            Assert.Equal(1, reset.OfflineDepth);
            Assert.Equal("reset:health-stall", queue.Stage);
            Assert.False(queue.TryTakeNext(generation, Now, out next));
            Assert.True(queue.PumpTask.IsCompleted);
        }

        [Fact]
        public void Reset_without_requeue_drops_the_held_item()
        {
            var queue = NewQueue();
            queue.Enqueue(new Item("held", false));
            int generation;
            queue.TryStart(_ => Task.CompletedTask, Now, out generation);
            Item next;
            queue.TryTakeNext(generation, Now, out next);

            IncomingQueueReset reset = queue.Reset("message-timeout-skip", requeueCurrent: false, Now);

            Assert.Equal(0, reset.LiveDepth);
            Assert.Null(queue.Snapshot().CurrentMessageId);
        }

        [Fact]
        public void Finish_clears_only_the_item_this_drain_still_holds()
        {
            var queue = NewQueue();
            var item = new Item("a", false);
            queue.Enqueue(item);
            int generation;
            queue.TryStart(_ => Task.CompletedTask, Now, out generation);
            Item next;
            queue.TryTakeNext(generation, Now, out next);

            queue.Finish(generation + 1, item, Now);
            Assert.Equal("a", queue.Snapshot().CurrentMessageId);
            queue.Finish(generation, new Item("other", false), Now);
            Assert.Equal("a", queue.Snapshot().CurrentMessageId);
            queue.Finish(generation, item, Now);
            Assert.Null(queue.Snapshot().CurrentMessageId);
            Assert.Equal("next", queue.Stage);
        }

        [Fact]
        public void Set_stage_keeps_the_current_item_when_none_is_given()
        {
            var queue = NewQueue();
            var item = new Item("a", false);

            queue.SetStage("render", item, Now);
            queue.SetStage(" ", null, Now);

            Assert.Equal("unknown", queue.Stage);
            Assert.Equal("a", queue.Snapshot().CurrentMessageId);
        }

        [Fact]
        public void A_timed_out_id_is_retried_once_then_skipped_until_it_succeeds()
        {
            var queue = NewQueue();
            var item = new Item("slow", false);

            Assert.True(queue.ShouldRetryTimedOut(item));
            Assert.False(queue.ShouldRetryTimedOut(item));
            queue.ForgetTimeout(item);
            Assert.True(queue.ShouldRetryTimedOut(item));
            Assert.True(queue.ShouldRetryTimedOut(new Item(" ", false)));
            Assert.True(queue.ShouldRetryTimedOut(new Item(" ", false)));
        }

        [Fact]
        public void A_running_pump_with_work_stalls_after_the_limit()
        {
            var queue = NewQueue();
            queue.Enqueue(new Item("a", false));
            int generation;
            queue.TryStart(_ => Task.CompletedTask, Now, out generation);

            Assert.False(queue.IsStalled(TimeSpan.FromSeconds(10), Now.AddSeconds(10)));
            Assert.True(queue.IsStalled(TimeSpan.FromSeconds(10), Now.AddSeconds(11)));
        }

        [Fact]
        public void Snapshot_reports_both_lanes_and_the_generation()
        {
            var queue = NewQueue();
            queue.Enqueue(new Item("a", false));
            queue.Enqueue(new Item("b", true));
            queue.Enqueue(new Item("c", true));
            queue.Enqueue(null);

            IncomingQueueSnapshot snapshot = queue.Snapshot();

            Assert.Equal(1, snapshot.LiveDepth);
            Assert.Equal(2, snapshot.OfflineDepth);
            Assert.Equal(0, snapshot.Generation);
            Assert.False(snapshot.Running);
            Assert.Equal("idle", snapshot.Stage);
        }
    }
}
