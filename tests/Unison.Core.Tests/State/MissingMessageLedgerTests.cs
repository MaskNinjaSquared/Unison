using System;
using System.Collections.Generic;
using System.Threading;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public sealed class MissingMessageLedgerTests
    {
        [Fact]
        public void Noting_a_message_twice_keeps_the_first_sight_and_refreshes_the_last()
        {
            var ledger = new MissingMessageLedger();
            DateTime first = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime second = first.AddMinutes(2);

            ledger.Note("111@s.whatsapp.net", "m1", "p1", false, first, "gap", first);
            ledger.Note("111@s.whatsapp.net", "m1", "p2", true, second, "again", second);

            MissingMessageCandidate candidate;
            Assert.True(ledger.TryGet("111@s.whatsapp.net", "m1", out candidate));
            Assert.Equal(first, candidate.FirstSeenUtc);
            Assert.Equal(second, candidate.LastSeenUtc);
            Assert.Equal("p2", candidate.Participant);
            Assert.True(candidate.IsFromMe);
            Assert.Equal("again", candidate.Reason);
        }

        [Fact]
        public void Resolving_removes_the_message_and_hands_back_its_scheduled_resend()
        {
            var ledger = new MissingMessageLedger();
            DateTime now = DateTime.UtcNow;
            ledger.Note("111@s.whatsapp.net", "m1", null, false, now, "gap", now);
            MissingMessageCandidate candidate;
            ledger.TryGet("111@s.whatsapp.net", "m1", out candidate);
            candidate.PlaceholderScheduleCts = new CancellationTokenSource();
            candidate.LastPlaceholderRequestId = "stanza-1";
            ledger.Resends["stanza-1"] = new PlaceholderResendRequestState { MessageId = "m1" };

            MissingMessageRemoval removal = ledger.Resolve("111@s.whatsapp.net", "m1");

            Assert.NotNull(removal.ScheduledResend);
            Assert.Equal("stanza-1", removal.RequestId);
            Assert.False(ledger.TryGet("111@s.whatsapp.net", "m1", out candidate));
            Assert.False(ledger.Resends.ContainsKey("stanza-1"));
        }

        [Fact]
        public void Forgetting_a_chat_drops_every_pending_repair_for_it()
        {
            var ledger = new MissingMessageLedger();
            DateTime now = DateTime.UtcNow;
            ledger.Note("111@s.whatsapp.net", "m1", null, false, now, "gap", now);
            ledger.Note("111@s.whatsapp.net", "m2", null, false, now, "gap", now);
            ledger.Note("222@s.whatsapp.net", "m3", null, false, now, "gap", now);
            MissingMessageCandidate first;
            ledger.TryGet("111@s.whatsapp.net", "m1", out first);
            first.LastPlaceholderRequestId = "stanza-1";
            ledger.Resends["stanza-1"] = new PlaceholderResendRequestState();

            IReadOnlyList<MissingMessageCandidate> dropped = ledger.ForgetChat("111@s.whatsapp.net");

            Assert.Equal(2, dropped.Count);
            Assert.False(ledger.Resends.ContainsKey("stanza-1"));
            Assert.True(ledger.TryGet("222@s.whatsapp.net", "m3", out first));
        }

        [Fact]
        public void A_blank_address_or_id_is_ignored()
        {
            var ledger = new MissingMessageLedger();
            ledger.Note(" ", "m1", null, false, DateTime.UtcNow, "gap", DateTime.UtcNow);
            ledger.Note("111@s.whatsapp.net", " ", null, false, DateTime.UtcNow, "gap", DateTime.UtcNow);

            Assert.Null(ledger.Resolve(" ", "m1").ScheduledResend);
            Assert.Empty(ledger.ForgetChat(null));
        }

        [Fact]
        public void The_drain_asks_a_direct_chat_before_a_group_and_stops_at_the_limit()
        {
            var ledger = new MissingMessageLedger();
            DateTime older = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            ledger.Note("999@g.us", "group", null, false, older, "gap", older);
            ledger.Note("111@s.whatsapp.net", "direct", null, false, older, "gap", older);
            MissingMessageCandidate busy;
            ledger.TryGet("999@g.us", "group", out busy);
            ledger.Note("999@g.us", "busy", null, false, older, "gap", older);
            ledger.TryGet("999@g.us", "busy", out busy);
            busy.PlaceholderRequestInFlight = true;

            int total;
            var selected = ledger.SelectForDrain(1, out total);

            Assert.Equal(2, total);
            Assert.Equal("direct", selected[0].MessageId);
        }

        [Fact]
        public void Begin_schedule_arms_the_timer_and_a_second_call_does_not()
        {
            MissingMessageCandidate candidate = Noted("m1");
            DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            CancellationTokenSource schedule = _ledger.TryBeginSchedule(candidate, now, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(5));

            Assert.NotNull(schedule);
            Assert.Same(schedule, candidate.PlaceholderScheduleCts);
            Assert.Equal(now.AddSeconds(5), candidate.PlaceholderScheduledForUtc);
            Assert.Null(_ledger.TryBeginSchedule(candidate, now, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(5)));
            schedule.Dispose();
        }

        [Fact]
        public void Claim_takes_only_the_timer_that_is_still_on_the_row()
        {
            MissingMessageCandidate candidate = Noted("m1");
            var schedule = new CancellationTokenSource();
            candidate.PlaceholderScheduleCts = schedule;
            var other = new CancellationTokenSource();

            MissingMessageCandidate claimed;
            Assert.False(_ledger.TryClaimScheduled("111@s.whatsapp.net", "m1", other, out claimed));
            Assert.Same(schedule, candidate.PlaceholderScheduleCts);
            Assert.True(_ledger.TryClaimScheduled("111@s.whatsapp.net", "m1", schedule, out claimed));
            Assert.True(claimed.PlaceholderRequestInFlight);
            Assert.Null(claimed.PlaceholderScheduleCts);
            Assert.Equal(DateTime.MinValue, claimed.PlaceholderScheduledForUtc);
            schedule.Dispose();
            other.Dispose();
        }

        [Fact]
        public void Record_send_counts_the_try_and_keeps_the_stanza()
        {
            Noted("m1");
            DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            MissingMessageCandidate candidate;
            Assert.False(_ledger.RecordSend("111@s.whatsapp.net", "gone", "stanza", "trigger", now, out candidate));
            Assert.True(_ledger.RecordSend("111@s.whatsapp.net", "m1", "stanza", "trigger", now, out candidate));

            Assert.Equal(1, candidate.PlaceholderRequestCount);
            Assert.Equal("stanza", candidate.LastPlaceholderRequestId);
            Assert.Equal(now, candidate.LastPlaceholderRequestUtc);
            Assert.Equal("trigger", _ledger.Resends["stanza"].Trigger);
            Assert.Equal(now, _ledger.Resends["stanza"].RequestedAtUtc);
        }

        [Fact]
        public void Expire_drops_the_stanza_and_clears_in_flight_only_for_that_id()
        {
            MissingMessageCandidate candidate = Noted("m1");
            candidate.PlaceholderRequestInFlight = true;
            candidate.LastPlaceholderRequestId = "stanza";
            _ledger.Resends["stanza"] = new PlaceholderResendRequestState
            {
                ChatJid = candidate.ChatJid,
                MessageId = candidate.MessageId,
                AckAccepted = true
            };

            Assert.Null(_ledger.Expire("other"));
            PlaceholderResendRequestState expired = _ledger.Expire("stanza");

            Assert.True(expired.AckAccepted);
            Assert.False(_ledger.Resends.ContainsKey("stanza"));
            Assert.False(candidate.PlaceholderRequestInFlight);
        }

        [Fact]
        public void Fail_send_returns_the_try_when_this_stanza_owned_it()
        {
            MissingMessageCandidate candidate = Noted("m1");
            candidate.PlaceholderRequestInFlight = true;
            candidate.PlaceholderRequestCount = 1;
            candidate.LastPlaceholderRequestId = "stanza";
            _ledger.Resends["stanza"] = new PlaceholderResendRequestState();

            _ledger.FailSend("111@s.whatsapp.net", "m1", "stanza");

            Assert.False(candidate.PlaceholderRequestInFlight);
            Assert.Null(candidate.LastPlaceholderRequestId);
            Assert.Equal(0, candidate.PlaceholderRequestCount);
            Assert.False(_ledger.Resends.ContainsKey("stanza"));
        }

        [Fact]
        public void A_failed_send_without_a_stanza_does_not_spend_an_older_try()
        {
            MissingMessageCandidate candidate = Noted("m1");
            candidate.PlaceholderRequestInFlight = true;
            candidate.PlaceholderRequestCount = 1;
            candidate.LastPlaceholderRequestId = "older";

            _ledger.FailSend("111@s.whatsapp.net", "m1", null);

            Assert.False(candidate.PlaceholderRequestInFlight);
            Assert.Equal("older", candidate.LastPlaceholderRequestId);
            Assert.Equal(1, candidate.PlaceholderRequestCount);
        }

        [Fact]
        public void Peer_response_clears_in_flight_and_a_blank_stanza_is_ignored()
        {
            MissingMessageCandidate candidate = Noted("m1");
            candidate.PlaceholderRequestInFlight = true;
            candidate.LastPlaceholderRequestId = "stanza";
            _ledger.Resends["stanza"] = new PlaceholderResendRequestState
            {
                ChatJid = candidate.ChatJid,
                MessageId = candidate.MessageId
            };

            Assert.Null(_ledger.TakePeerResponse(" "));
            Assert.True(_ledger.Resends.ContainsKey("stanza"));
            PlaceholderResendRequestState taken = _ledger.TakePeerResponse("stanza");

            Assert.Equal("m1", taken.MessageId);
            Assert.False(candidate.PlaceholderRequestInFlight);
            Assert.False(_ledger.Resends.ContainsKey("stanza"));
        }

        [Fact]
        public void An_ack_rejection_clears_the_row_and_an_acceptance_only_marks_it()
        {
            MissingMessageCandidate candidate = Noted("m1");
            candidate.PlaceholderRequestInFlight = true;
            candidate.LastPlaceholderRequestId = "stanza";
            DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            _ledger.Resends["stanza"] = new PlaceholderResendRequestState
            {
                ChatJid = candidate.ChatJid,
                MessageId = candidate.MessageId
            };

            PlaceholderResendRequestState request;
            Assert.False(_ledger.TryApplyAck("missing", false, now, out request));
            Assert.True(_ledger.TryApplyAck("stanza", false, now, out request));
            Assert.True(request.AckAccepted);
            Assert.Equal(now, request.AckAcceptedUtc);
            Assert.True(candidate.PlaceholderRequestInFlight);

            Assert.True(_ledger.TryApplyAck("stanza", true, now, out request));
            Assert.False(candidate.PlaceholderRequestInFlight);
            Assert.False(_ledger.Resends.ContainsKey("stanza"));
        }

        [Fact]
        public void Reconcile_skips_while_the_quiet_period_is_still_running()
        {
            DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var ledger = new MissingMessageLedger();

            Assert.True(ledger.TryBeginReconcile("111@s.whatsapp.net", now, TimeSpan.FromSeconds(12)));
            Assert.False(ledger.TryBeginReconcile("111@s.whatsapp.net", now.AddSeconds(11), TimeSpan.FromSeconds(12)));
            Assert.True(ledger.TryBeginReconcile("111@s.whatsapp.net", now.AddSeconds(12), TimeSpan.FromSeconds(12)));
        }

        [Fact]
        public void Pending_ids_follow_dictionary_order_and_the_budget()
        {
            var ledger = new MissingMessageLedger();
            DateTime now = DateTime.UtcNow;
            ledger.Note("111@s.whatsapp.net", "a", null, false, now, "gap", now);
            ledger.Note("111@s.whatsapp.net", "b", null, false, now, "gap", now);
            ledger.Note("111@s.whatsapp.net", "c", null, false, now, "gap", now);

            Assert.Equal(new[] { "a", "b" }, ledger.CopyPendingIds("111@s.whatsapp.net", 2));
            Assert.Empty(ledger.CopyPendingIds("222@s.whatsapp.net", 2));
        }

        private readonly MissingMessageLedger _ledger = new MissingMessageLedger();

        private MissingMessageCandidate Noted(string messageId)
        {
            DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            _ledger.Note("111@s.whatsapp.net", messageId, "p", false, now, "gap", now);
            MissingMessageCandidate candidate;
            _ledger.TryGet("111@s.whatsapp.net", messageId, out candidate);
            return candidate;
        }
    }
}
