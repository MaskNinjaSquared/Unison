// =============================================================================
// PostReplayMaintenanceGates
//
// Pure post-replay abort check. Quiet window and delayed bulk are product knobs
// (IStartupMaintenanceTuning) read through the host.
// =============================================================================

namespace Unison.WhatsAppClient.Helpers
{
    public static class PostReplayMaintenanceGates
    {
        public static bool ShouldAbortBeforeWork(
            bool quietReached,
            bool isConnected,
            bool isWindowVisible)
        {
            return !quietReached || !isConnected || !isWindowVisible;
        }
    }
}
