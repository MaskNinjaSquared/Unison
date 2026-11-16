// =============================================================================
// FullHistoryAckRejectedDispatch
//
// A rejected FULL_HISTORY ack has already cleared the registry, so no watchdog
// or quiet lot will ever end the catch-up it opened. Finish it here or the
// banner, enrichment (avatars) and toast suppression stay up for the session.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class FullHistoryAckRejectedDispatch
    {
        public const string FinishReason = "full-history-ack-rejected";

        public static void Run(
            IFullHistoryAckRejectedHost host,
            string requestId,
            string error,
            string triggerReason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            bool catchUpActive = host.IsHistoryCatchUpActive;
            host.WriteFullHistoryAckRejected(
                "requestId=" + (requestId ?? string.Empty) +
                "; error=" + (error ?? string.Empty) +
                "; trigger=" + (triggerReason ?? string.Empty) +
                "; catchUpActive=" + catchUpActive);

            host.RaiseFullHistoryRejectedStatus();
            host.CompleteUserResyncOnFullHistoryAckRejected();

            if (catchUpActive)
            {
                host.FinishHistoryCatchUp(FinishReason, releaseEnrichment: true);
            }
        }
    }
}
