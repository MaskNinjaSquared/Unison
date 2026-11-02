// =============================================================================
// SilentFullHistoryPolicy
//
// When to fire the next silent FULL_HISTORY_SYNC_ON_DEMAND after a quiet lot, and
// whether that lot should surface the catch-up banner. Delays are short probes
// per device tier; the host must wait for socket+DB quiet before scheduling.
// Caps consecutive empty lots so Mobile does not probe forever.
// =============================================================================
using System;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public enum SilentFullHistoryNext
    {
        /// <summary>Do not arm another silent FULL yet.</summary>
        None,

        /// <summary>Wait <see cref="SilentFullHistoryPolicy.DelayFor"/> then fire one silent FULL.</summary>
        ScheduleAfterDelay,

        /// <summary>
        /// Too many consecutive empty quiet lots — disarm the silent chain for this session.
        /// </summary>
        CapReached
    }

    public static class SilentFullHistoryPolicy
    {
        public const string ReasonPrefix = "silent-full:";

        /// <summary>Stop soliciting after this many consecutive quiet lots with no applied data.</summary>
        public const int MaxConsecutiveEmptyLots = 4;

        public static bool IsSilentReason(string reason)
        {
            return !string.IsNullOrEmpty(reason) &&
                   reason.StartsWith(ReasonPrefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// Arm the silent chain when health starts only if stagnant cascade (and the
        /// periodic safety tick that mirrors it) are disabled — Low / Mobile. High
        /// already recovers via cascade + periodic FULL safety; arming both would
        /// double-solicit.
        /// </summary>
        public static bool ShouldArmOnHealthStart(bool allowStagnantRepairCascade)
        {
            return !allowStagnantRepairCascade;
        }

        /// <summary>
        /// After a loud catch-up finish (offline-fresh / stagnant / etc.), kick the
        /// first silent probe only when the chain is armed and tips still look owed.
        /// Silent-lot finishes go through <see cref="AfterQuietLot"/> instead.
        /// </summary>
        public static bool ShouldKickAfterLoudCatchUpFinish(
            bool armed,
            bool tipStale,
            string finishReason)
        {
            if (!armed || !tipStale)
            {
                return false;
            }

            if (string.IsNullOrEmpty(finishReason))
            {
                return true;
            }

            if (IsSilentReason(finishReason))
            {
                return false;
            }

            if (string.Equals(finishReason, "silent-lot-idle", StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Tip already fresh while armed — disarm rather than spin skip-fire forever.
        /// </summary>
        public static bool ShouldStopBecauseTipFresh(bool armed, bool tipStale)
        {
            return armed && !tipStale;
        }

        public static TimeSpan DelayFor(DeviceCategory category)
        {
            switch (category)
            {
                case DeviceCategory.High:
                    return TimeSpan.FromMinutes(2);
                case DeviceCategory.Low:
                    return TimeSpan.FromMinutes(3);
                default:
                    return TimeSpan.FromMinutes(5);
            }
        }

        /// <summary>Banner when the quiet lot (or live progress) actually applied messages.</summary>
        public static bool ShouldShowBanner(int appliedCount)
        {
            return appliedCount > 0;
        }

        /// <summary>
        /// Updates the empty-streak counter: productive lot resets to 0; empty increments.
        /// </summary>
        public static int NextConsecutiveEmpty(int previousConsecutiveEmpty, int appliedCount)
        {
            if (appliedCount > 0)
            {
                return 0;
            }

            if (previousConsecutiveEmpty < 0)
            {
                previousConsecutiveEmpty = 0;
            }

            return previousConsecutiveEmpty + 1;
        }

        /// <summary>
        /// After a silent lot has gone quiet (chunks applied + SQLite done), schedule the
        /// next probe when the claim is free — unless the empty-cap is reached.
        /// </summary>
        public static SilentFullHistoryNext AfterQuietLot(
            bool lotWasSilent,
            bool fullClaimed,
            int consecutiveEmptyAfterThisLot,
            out string reason)
        {
            if (!lotWasSilent)
            {
                reason = "not-silent";
                return SilentFullHistoryNext.None;
            }

            if (fullClaimed)
            {
                reason = "full-still-claimed";
                return SilentFullHistoryNext.None;
            }

            if (consecutiveEmptyAfterThisLot >= MaxConsecutiveEmptyLots)
            {
                reason = "empty-cap";
                return SilentFullHistoryNext.CapReached;
            }

            reason = "schedule-next";
            return SilentFullHistoryNext.ScheduleAfterDelay;
        }

        /// <summary>
        /// Gate for firing a silent FULL: tip still stale, socket up, no catch-up
        /// claim, no FULL in flight, and the previous lot is quiet.
        /// </summary>
        public static bool CanFire(
            bool socketReady,
            bool catchUpActive,
            bool fullClaimed,
            bool previousLotQuiet,
            bool tipStale,
            out string reason)
        {
            if (!tipStale)
            {
                reason = "tip-fresh";
                return false;
            }

            if (!socketReady)
            {
                reason = "socket-not-ready";
                return false;
            }

            if (!previousLotQuiet)
            {
                reason = "previous-lot-busy";
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

            reason = "ok";
            return true;
        }
    }
}
