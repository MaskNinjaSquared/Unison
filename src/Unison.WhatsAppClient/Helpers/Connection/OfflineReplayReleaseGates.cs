// =============================================================================
// OfflineReplayReleaseGates
//
// Pure branches inside the pending-notification release handler.
// =============================================================================

namespace Unison.WhatsAppClient.Helpers
{
    public static class OfflineReplayReleaseGates
    {
        /// <summary>
        /// Safety-timeout release can race a hung ConnectAsync; never mark synced
        /// until Noise finished on the live socket.
        /// </summary>
        public static bool ShouldSkipSyncedMark(bool firstRelease, bool transportReady)
        {
            return firstRelease && !transportReady;
        }

        /// <summary>
        /// Soft-reconnect catch-up pages must re-evaluate freshness even when this
        /// is not the first offline release of the process.
        /// </summary>
        public static bool ShouldLogHistoryFreshness(bool firstRelease, bool catchUpActive)
        {
            return firstRelease || catchUpActive;
        }
    }
}
