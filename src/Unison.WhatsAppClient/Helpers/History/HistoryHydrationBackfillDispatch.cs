// =============================================================================
// HistoryHydrationBackfillDispatch
//
// Multi-batch ON_DEMAND hydration loop. Lock / CAS / candidate build stay on
// the host; abort / continue / flight classify already live beside the ledger.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class HistoryHydrationBackfillDispatch
    {
        public static async Task<int> RunAsync(
            IHistoryHydrationBackfillHost host,
            string reason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (!host.IsConnected)
            {
                host.LogHydrationSkippedNotConnected(reason);
                return 0;
            }

            IReadOnlyList<HistoryBackfillCandidate> selected =
                host.BuildHydrationCandidates(DateTime.UtcNow);
            if (selected == null || selected.Count == 0)
            {
                host.LogHydrationNoCandidates(reason);
                return 0;
            }

            var ledger = new HistoryBackfillLedger(host.HydrationMaxPagesPerChat);
            ledger.Enqueue(selected);
            host.WriteHydrationStart(
                reason,
                selected.Count,
                host.HydrationBatchSize,
                host.HydrationMaxPagesPerChat);

            int requested = 0;
            int batchIndex = 0;
            while (ledger.HasWorkRemaining && host.IsConnected)
            {
                batchIndex++;
                IReadOnlyList<HistoryBackfillLedgerEntry> batch = ledger.TakeBatch(
                    host.HydrationBatchSize,
                    host.GetHydrationMessageCount);
                if (batch.Count == 0)
                {
                    break;
                }

                var sentJids = new List<string>(batch.Count);
                for (int i = 0; i < batch.Count; i++)
                {
                    if (!host.IsConnected)
                    {
                        break;
                    }

                    HistoryBackfillLedgerEntry entry = batch[i];
                    if (entry == null || string.IsNullOrWhiteSpace(entry.Jid))
                    {
                        continue;
                    }

                    string normJid = host.NormalizeHydrationJid(entry.Jid);
                    if (ledger.TryGet(normJid, out HistoryBackfillLedgerEntry live))
                    {
                        live.BaselineCount = host.GetHydrationMessageCount(normJid);
                    }

                    bool sent = await host.RequestHydrationOnDemandAsync(
                            normJid,
                            host.HydrationPageSize,
                            "hydration:batch" + batchIndex + ":" + reason)
                        .ConfigureAwait(false);
                    if (sent)
                    {
                        ledger.NoteRequestSent(normJid);
                        sentJids.Add(normJid);
                        requested++;
                    }
                    else
                    {
                        ledger.MarkSynced(normJid);
                    }
                }

                await host.WaitForHydrationSettleAsync().ConfigureAwait(false);

                int outcomeAckSilent = 0;
                int outcomeRamGrew = 0;
                int outcomeNoAck = 0;
                int outcomeRejected = 0;
                for (int i = 0; i < sentJids.Count; i++)
                {
                    string jid = sentJids[i];
                    int afterRam = host.GetHydrationMessageCount(jid);
                    int baselineRam = 0;
                    if (ledger.TryGet(jid, out HistoryBackfillLedgerEntry before) &&
                        before != null)
                    {
                        baselineRam = before.BaselineCount;
                    }

                    var obs = new HistoryBackfillFlightObservation
                    {
                        HadRequest = true,
                        AckAccepted = true,
                        BaselineRam = baselineRam,
                        AfterRam = afterRam,
                        ChunkApplied = afterRam > baselineRam
                    };
                    HistoryBackfillFlightOutcome outcome =
                        HistoryBackfillFlightClassifier.Classify(obs);
                    switch (outcome)
                    {
                        case HistoryBackfillFlightOutcome.RamGrew:
                            outcomeRamGrew++;
                            break;
                        case HistoryBackfillFlightOutcome.AckSilent:
                        case HistoryBackfillFlightOutcome.ChunkNoRamGrowth:
                            outcomeAckSilent++;
                            break;
                        case HistoryBackfillFlightOutcome.NoAck:
                            outcomeNoAck++;
                            break;
                        case HistoryBackfillFlightOutcome.Rejected:
                            outcomeRejected++;
                            break;
                    }

                    ledger.CompleteFlight(jid, afterRam, outcome);
                }

                for (int i = 0; i < batch.Count; i++)
                {
                    HistoryBackfillLedgerEntry entry = batch[i];
                    if (entry != null && entry.State == HistoryBackfillChatState.InFlight)
                    {
                        ledger.CompleteFlight(
                            entry.Jid,
                            host.GetHydrationMessageCount(entry.Jid));
                    }
                }

                bool silentAbort = HistoryBackfillAbortDecision.ShouldAbortAfterSilentBatch(
                    sentJids.Count,
                    outcomeAckSilent,
                    outcomeNoAck,
                    outcomeRejected,
                    outcomeRamGrew);
                HistoryBackfillContinueAction cont = HistoryBackfillContinueDecision.Decide(
                    ledger.HasWorkRemaining,
                    silentAbort);
                host.WriteHydrationBatch(
                    batchIndex,
                    sentJids.Count,
                    outcomeRamGrew,
                    ledger.CountInState(HistoryBackfillChatState.HasMore),
                    cont);
                if (cont != HistoryBackfillContinueAction.Continue)
                {
                    break;
                }
            }

            host.LogHydrationDone(
                requested,
                ledger.CountInState(HistoryBackfillChatState.Synced),
                ledger.CountInState(HistoryBackfillChatState.HasMore),
                ledger.CountInState(HistoryBackfillChatState.Failed));
            return requested;
        }
    }
}
