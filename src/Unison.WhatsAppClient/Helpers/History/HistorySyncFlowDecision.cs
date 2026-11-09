// =============================================================================
// HistorySyncFlowDecision
//
// PairingBootstrap (first history after pair / empty SQLite) vs PostInitCatchUp
// (reconnect / already-have-history). Catch-up finish and FULL_HISTORY
// solicitation must not own the chat-list unlock during PairingBootstrap.
// =============================================================================

namespace Unison.WhatsAppClient.Helpers
{
    public static class HistorySyncFlowDecision
    {
        /// <summary>
        /// True while the first pairing history sync still owns the list hold:
        /// awaiting first history, a non-on-demand apply in flight, or a bootstrap
        /// chunk seen this session that has not yet quiet-finalized.
        /// </summary>
        public static bool IsPairingBootstrapActive(
            bool awaitingFirstHistorySync,
            bool bootstrapApplyInFlight,
            bool bootstrapChunkSeenPendingFinalize)
        {
            return awaitingFirstHistorySync
                || bootstrapApplyInFlight
                || bootstrapChunkSeenPendingFinalize;
        }

        /// <summary>
        /// Post-init FULL_HISTORY (message-store-epoch, idle-continue, offline-stale)
        /// must not run while PairingBootstrap owns the session.
        /// </summary>
        public static bool ShouldSolicitPostInitFullHistory(bool pairingBootstrapActive)
        {
            return !pairingBootstrapActive;
        }

        /// <summary>
        /// Adjacent FULL_HISTORY continue after a quiet lot is PostInit-only.
        /// </summary>
        public static bool ShouldRequestAdjacentCatchUp(bool pairingBootstrapActive)
        {
            return !pairingBootstrapActive;
        }
    }
}
