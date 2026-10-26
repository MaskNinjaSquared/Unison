// =============================================================================
// HistoryPeriodicFullSafetyDecision
//
// Whether the idle FULL_HISTORY safety tick should solicit again. Standby
// devices that took a FULL lot but never closed catch-up get another chance;
// healthy accounts with a fresh tip and no repair latch stay quiet.
// =============================================================================
using System;
using Unison.Core.Helpers;

namespace Unison.WhatsAppClient.Helpers
{
    public static class HistoryPeriodicFullSafetyDecision
    {
        /// <summary>How often the host re-evaluates while the socket is up.</summary>
        public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

        /// <summary>
        /// True when a FULL_HISTORY on-demand should go out. <paramref name="reason"/> is always set.
        /// </summary>
        /// <param name="allowStagnantRepairCascade">
        /// When false (Low / Unsupported), never re-solicit from this tick — catch-up already
        /// finished stagnant without hydration / force-repair latch.
        /// </param>
        public static bool ShouldRequest(
            bool socketReady,
            bool catchUpActive,
            bool fullClaimed,
            bool forceHistoryRepairPending,
            bool tipStale,
            bool allowStagnantRepairCascade,
            out string reason)
        {
            if (!socketReady)
            {
                reason = "socket-not-ready";
                return false;
            }

            if (catchUpActive)
            {
                reason = "catch-up-active";
                return false;
            }

            if (fullClaimed)
            {
                reason = "full-claimed";
                return false;
            }

            if (!forceHistoryRepairPending && !tipStale)
            {
                reason = "no-debt";
                return false;
            }

            if (!allowStagnantRepairCascade)
            {
                reason = "cascade-disabled";
                return false;
            }

            if (forceHistoryRepairPending && tipStale)
            {
                reason = "repair-and-stale";
                return true;
            }

            if (forceHistoryRepairPending)
            {
                reason = "force-repair";
                return true;
            }

            reason = "tip-stale";
            return true;
        }
    }
}
