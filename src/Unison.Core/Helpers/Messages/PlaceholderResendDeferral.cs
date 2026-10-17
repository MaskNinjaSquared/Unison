// =============================================================================
// PlaceholderResendDeferral
//
// Why a placeholder resend waits. The first matching reason wins. A socket that
// is not up is not one of these reasons: the caller already refuses the send.
// =============================================================================
namespace Unison.Core.Helpers
{
    public static class PlaceholderResendDeferral
    {
        public static bool IsDeferred(
            bool reconnectReplayActive,
            bool awaitingInitialSync,
            bool historyOnDemandInFlight,
            out string reason)
        {
            if (reconnectReplayActive)
            {
                reason = "reconnect-replay-active";
                return true;
            }

            if (awaitingInitialSync)
            {
                reason = "awaiting-initial-sync";
                return true;
            }

            if (historyOnDemandInFlight)
            {
                reason = "history-on-demand-in-flight";
                return true;
            }

            reason = null;
            return false;
        }
    }
}
