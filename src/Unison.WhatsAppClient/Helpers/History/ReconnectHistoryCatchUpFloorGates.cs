// =============================================================================
// ReconnectHistoryCatchUpFloorGates
//
// Quiet / visibility floor before RequestReconnectHistoryCatchUpAsync solicits
// FULL_HISTORY. Mid catch-up skips the window-visibility gate.
// =============================================================================

namespace Unison.WhatsAppClient.Helpers
{
    public enum ReconnectHistoryCatchUpFloorAction
    {
        Proceed,
        /// <summary>Abort and release enrichment (cold request, not mid catch-up).</summary>
        AbortAndReleaseEnrichment,
        /// <summary>Abort but keep enrichment latched (mid catch-up).</summary>
        AbortKeepEnrichment
    }

    public static class ReconnectHistoryCatchUpFloorGates
    {
        public static ReconnectHistoryCatchUpFloorAction Decide(
            bool quietSettled,
            bool isConnected,
            bool catchUpActive,
            bool windowVisible)
        {
            bool abort =
                !quietSettled ||
                !isConnected ||
                (!catchUpActive && !windowVisible);

            if (!abort)
            {
                return ReconnectHistoryCatchUpFloorAction.Proceed;
            }

            return catchUpActive
                ? ReconnectHistoryCatchUpFloorAction.AbortKeepEnrichment
                : ReconnectHistoryCatchUpFloorAction.AbortAndReleaseEnrichment;
        }
    }
}
