// =============================================================================
// PostReplayMaintenanceGates
//
// Pure post-replay schedule decisions (quiet wait, delayed bulk tip scan).
// =============================================================================
using System;

namespace Unison.WhatsAppClient.Helpers
{
    public static class PostReplayMaintenanceGates
    {
        public static TimeSpan QuietWait(bool useFrugalSyncBudget)
        {
            return useFrugalSyncBudget
                ? TimeSpan.FromSeconds(8)
                : TimeSpan.FromSeconds(12);
        }

        /// <summary>
        /// Large offline drains get a delayed bulk tip scan unless the device is
        /// already on a frugal sync budget.
        /// </summary>
        public static bool ShouldRunDelayedBulk(int offlineCount, bool useFrugalSyncBudget)
        {
            return offlineCount >= 50 && !useFrugalSyncBudget;
        }

        public static bool ShouldAbortBeforeWork(
            bool quietReached,
            bool isConnected,
            bool isWindowVisible)
        {
            return !quietReached || !isConnected || !isWindowVisible;
        }
    }
}
