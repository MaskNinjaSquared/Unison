// =============================================================================
// ReleaseEnrichmentAfterHeavyHistoryDispatch
//
// Body of ReleaseEnrichmentAfterHeavyHistory. Enrichment scheduling stays on
// the host.
// =============================================================================
using System;
using Unison.Core.Helpers;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class ReleaseEnrichmentAfterHeavyHistoryDispatch
    {
        public static void Run(IReleaseEnrichmentAfterHeavyHistoryHost host, string reason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            // Keep names/avatars/groups deferred while catch-up is still owed — including Low
            // (MaxFullHistoryCatchUpRounds == 0). Starting enrich mid catch-up races usync
            // against chronological offline / FULL_HISTORY delivery on Mobile.
            if (HistoryCatchUpLocalWorkPolicy.ShouldDeferHeavyLocalWork(host.IsHistoryCatchUpActive))
            {
                host.MarkEnrichmentAwaitingHeavyHistory();
                host.LogEnrichmentStillDeferred(reason);
                host.WriteEnrichmentDeferredCatchUp(reason);
                return;
            }

            host.ClearEnrichmentAwaitingHeavyHistory();
            host.TrySchedulePostMessageEnrichment("heavy-history:" + reason);
        }
    }
}
