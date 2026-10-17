// =============================================================================
// SyncPacing
//
// The waits and stop thresholds around startup and history catch-up. Each
// SyncWorkloadStrategy holds a frugal and a generous set; SyncBudget decides
// at run time which one applies (memory pressure, hot sync). The tier owns the
// numbers, the budget owns the switch.
// =============================================================================
namespace Unison.Core.State
{
    public sealed class SyncPacing
    {
        public SyncPacing(
            int startupQuietFloorMs,
            int deferredStartupQuietMs,
            int catchUpIdleAbortSeconds,
            int catchUpStagnantCyclesBeforeStop)
        {
            StartupQuietFloorMs = startupQuietFloorMs;
            DeferredStartupQuietMs = deferredStartupQuietMs;
            CatchUpIdleAbortSeconds = catchUpIdleAbortSeconds;
            CatchUpStagnantCyclesBeforeStop = catchUpStagnantCyclesBeforeStop;
        }

        /// <summary>Breathing room the UI gets before enrichment starts.</summary>
        public int StartupQuietFloorMs { get; }

        /// <summary>Quiet budget before deferred startup maintenance (repair, enrichment).</summary>
        public int DeferredStartupQuietMs { get; }

        /// <summary>Idle time between FULL_HISTORY chunks before the lot counts as quiet.</summary>
        public int CatchUpIdleAbortSeconds { get; }

        /// <summary>Lots without progress before catch-up stops insisting.</summary>
        public int CatchUpStagnantCyclesBeforeStop { get; }
    }
}
