// =============================================================================
// ISyncPhaseTuning
//
// Quiet floors and catch-up idle thresholds around startup and history
// catch-up. The product layer picks the tier / frugal set; the caller only
// supplies the live fact of whether the first-sync chat list is still held.
// =============================================================================
using System;

namespace Unison.Core.Contracts.Tuning
{
    public interface ISyncPhaseTuning
    {
        /// <summary>Breathing room the UI gets before enrichment starts.</summary>
        TimeSpan StartupQuietFloor(bool firstSyncListHold);

        /// <summary>Quiet budget before deferred startup maintenance.</summary>
        TimeSpan DeferredStartupQuietWait(bool firstSyncListHold);

        /// <summary>Idle time between FULL_HISTORY chunks before the lot counts as quiet.</summary>
        TimeSpan CatchUpIdleAbort { get; }
    }
}
