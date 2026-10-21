// =============================================================================
// Tests for HistoryBackfillLedger — Pending / InFlight / HasMore / Synced.
// =============================================================================
using System.Collections.Generic;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class HistoryBackfillLedgerTests
    {
        [Fact]
        public void Growth_requeues_as_HasMore()
        {
            var ledger = new HistoryBackfillLedger();
            ledger.Enqueue(new[] { Cand("a@s.whatsapp.net", 0) });
            IReadOnlyList<HistoryBackfillLedgerEntry> batch = ledger.TakeBatch(1, _ => 5);
            ledger.NoteRequestSent("a@s.whatsapp.net");
            Assert.Equal(
                HistoryBackfillChatState.HasMore,
                ledger.CompleteFlight("a@s.whatsapp.net", 12));
            Assert.True(ledger.HasWorkRemaining);
        }

        [Fact]
        public void No_growth_marks_Synced()
        {
            var ledger = new HistoryBackfillLedger();
            ledger.Enqueue(new[] { Cand("a@s.whatsapp.net", 0) });
            ledger.TakeBatch(1, _ => 5);
            ledger.NoteRequestSent("a@s.whatsapp.net");
            Assert.Equal(
                HistoryBackfillChatState.Synced,
                ledger.CompleteFlight("a@s.whatsapp.net", 5));
            Assert.False(ledger.HasWorkRemaining);
        }

        [Fact]
        public void Silent_abort_decision_when_all_unanswered()
        {
            Assert.True(
                HistoryBackfillAbortDecision.ShouldAbortAfterSilentBatch(
                    sent: 5, ackSilent: 5, noAck: 0, rejected: 0, ramGrew: 0));
            Assert.Equal(
                HistoryBackfillContinueAction.StopSilent,
                HistoryBackfillContinueDecision.Decide(
                    hasWorkRemaining: true,
                    silentBatchAbort: true));
        }

        private static HistoryBackfillCandidate Cand(string jid, int listIndex)
        {
            return new HistoryBackfillCandidate { Jid = jid, ListIndex = listIndex };
        }
    }
}
