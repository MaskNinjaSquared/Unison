// =============================================================================
// SyncPhasePacing
//
// Quiet floors while the first-sync chat list is still held. The list is not
// materializing, so the UI-protection sleeps in SyncPacing are overkill — shorten
// them for that phase only. Catch-up abort thresholds stay with the tier.
// =============================================================================
using System;

namespace Unison.Core.State
{
    public static class SyncPhasePacing
    {
        /// <summary>Hold-phase floor when the tier's frugal floor would wait longer.</summary>
        public const int HoldStartupQuietFloorMs = 1600;

        /// <summary>Hold-phase deferred quiet when the tier's frugal deferred would wait longer.</summary>
        public const int HoldDeferredStartupQuietMs = 2800;

        public static SyncPacing For(
            SyncWorkloadStrategy workload,
            bool frugal,
            bool firstSyncListHold)
        {
            if (workload == null)
            {
                throw new ArgumentNullException(nameof(workload));
            }

            SyncPacing basePacing = workload.PacingFor(frugal);
            if (!firstSyncListHold)
            {
                return basePacing;
            }

            return new SyncPacing(
                Math.Min(basePacing.StartupQuietFloorMs, HoldStartupQuietFloorMs),
                Math.Min(basePacing.DeferredStartupQuietMs, HoldDeferredStartupQuietMs),
                basePacing.CatchUpIdleAbortSeconds,
                basePacing.CatchUpStagnantCyclesBeforeStop);
        }
    }
}
