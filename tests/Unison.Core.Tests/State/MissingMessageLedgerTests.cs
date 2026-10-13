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
    }
}
