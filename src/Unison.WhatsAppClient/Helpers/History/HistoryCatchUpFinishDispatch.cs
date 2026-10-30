// =============================================================================
// HistoryCatchUpFinishDispatch
//
// Body of FinishHistoryCatchUpContinue after progress ends: journal, banner,
// then list-release or deferred enrichment per HistoryCatchUpFinishDecision.
// =============================================================================
using System;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class HistoryCatchUpFinishDispatch
    {
        public static void Run(
            IHistoryCatchUpFinishHost host,
            string reason,
            bool releaseEnrichment)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            HistoryCatchUpProgress last = host.FinishCatchUpProgress();
            host.WriteCatchUpFinishJournal(reason, last);
            host.EndHistoryCatchUpBanner(reason ?? "catch-up-finish");

            HistoryCatchUpFinishNext next = HistoryCatchUpFinishDecision.Decide(
                releaseEnrichment,
                host.IsFirstSyncListHoldInProgress,
                host.IsListAwaitingFirstHistorySync);
            if (next == HistoryCatchUpFinishNext.None)
            {
                return;
            }

            // Quiet-finalize may have deferred IsCompleted while catch-up pages still ran —
            // names, then list unlock, then photos (not on the first quiet).
            if (next == HistoryCatchUpFinishNext.ReleaseListAfterNames)
            {
                int processed = Math.Max(
                    host.SqliteHistoryConversationsAccumulated,
                    host.InitialSyncProcessedConversations);
                host.RequestFirstSyncListReleaseAfterNames(
                    "catch-up-finish:" + (reason ?? string.Empty),
                    processed,
                    Math.Max(processed, 1));
                return;
            }

            if (next == HistoryCatchUpFinishNext.ReleaseListThenEnrichment)
            {
                host.RequestFirstSyncListReleaseAfterNames(
                    "catch-up-finish-no-history:" + (reason ?? string.Empty),
                    0,
                    1);
            }

            host.ScheduleDeferredAvatars(
                "catch-up-finish:" + (reason ?? string.Empty),
                TimeSpan.FromSeconds(5));
            host.ClearEnrichmentAwaitingHeavyHistory();
            host.TrySchedulePostMessageEnrichment(
                "catch-up-finish:" + (reason ?? string.Empty));
        }
    }
}
