// =============================================================================
// FullHistoryAckDeadlineDispatch
//
// A catch-up FULL_HISTORY request only gets a watchdog once the server accepts
// it. With no ack, or with the request dropped by a path that does not end the
// banner, nothing else would ever finish the catch-up, and avatars and toasts
// stay held behind it. This deadline closes both holes.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class FullHistoryAckDeadlineDispatch
    {
        public const string AckMissingReason = "full-history-ack-missing";

        public const string OrphanedReason = "full-history-catch-up-orphaned";

        public static async Task RunAsync(IFullHistoryAckDeadlineHost host, string requestId)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (string.IsNullOrWhiteSpace(requestId))
            {
                return;
            }

            await host.DelayAsync(host.FullHistoryAckDeadline).ConfigureAwait(false);

            HistoryOnDemandRequest tracked = host.FindFullRequest(requestId);
            if (tracked != null)
            {
                if (!tracked.AckAccepted)
                {
                    ExpireUnacked(host, tracked);
                }

                return;
            }

            if (host.IsFullHistoryPending || !host.IsHistoryCatchUpBannerActive)
            {
                return;
            }

            host.WriteHistoryJournal(OrphanedReason, "requestId=" + requestId);
            host.FinishHistoryCatchUp(OrphanedReason, releaseEnrichment: true);
        }

        private static void ExpireUnacked(IFullHistoryAckDeadlineHost host, HistoryOnDemandRequest tracked)
        {
            if (!host.TryClearFullRequest(tracked))
            {
                return;
            }

            bool bannerActive = host.IsHistoryCatchUpBannerActive;
            host.WriteHistoryJournal(
                AckMissingReason,
                "requestId=" + tracked.RequestId +
                "; deadlineMs=" + (int)host.FullHistoryAckDeadline.TotalMilliseconds +
                "; trigger=" + (tracked.TriggerReason ?? string.Empty) +
                "; banner=" + bannerActive);
            if (bannerActive)
            {
                host.FinishHistoryCatchUp(AckMissingReason, releaseEnrichment: true);
            }
        }
    }
}
