// =============================================================================
// PostReplayMaintenanceCoordinator
//
// Unison product decisions for post-replay maintenance: quiet wait, delayed
// bulk tip scan, and abort when quiet / connection / visibility fail. The
// WhatsApp client runs the wait and notify; this type owns the knobs and abort.
// =============================================================================
using System;
using Unison.Core.Contracts.Tuning;

namespace Unison.Core.Product
{
    public sealed class PostReplayMaintenanceCoordinator
    {
        private readonly IStartupMaintenanceTuning _tuning;

        public PostReplayMaintenanceCoordinator(IStartupMaintenanceTuning tuning)
        {
            _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
        }

        public TimeSpan QuietWait => _tuning.PostReplayQuietWait;

        public bool RunDelayedBulkAfterReplay(int offlineCount) =>
            _tuning.RunDelayedBulkAfterReplay(offlineCount);

        public static bool ShouldAbortBeforeWork(
            bool quietReached,
            bool isConnected,
            bool isWindowVisible) =>
            !quietReached || !isConnected || !isWindowVisible;
    }
}
