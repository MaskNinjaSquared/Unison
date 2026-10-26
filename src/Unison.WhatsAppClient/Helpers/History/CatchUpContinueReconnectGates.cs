// =============================================================================
// CatchUpContinueReconnectGates
//
// Whether ScheduleCatchUpContinueReconnect should start soft-reconnect or finish
// catch-up early. Delay / socket tear-down stay on the host.
// =============================================================================
using System;

namespace Unison.WhatsAppClient.Helpers
{
    public enum CatchUpContinueScheduleAction
    {
        /// <summary>Another reconnect already owns the slot — finish with continue-busy.</summary>
        FinishBusy,

        /// <summary>Reconnect suppressed — finish with continue-suppress.</summary>
        FinishSuppress,

        /// <summary>No registered auth — finish with continue-unregistered.</summary>
        FinishUnregistered,

        /// <summary>Claim the slot and schedule soft-reconnect (cooldown only slows logging).</summary>
        StartReconnect
    }

    public static class CatchUpContinueReconnectGates
    {
        public static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(12);

        public static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(15);

        public static CatchUpContinueScheduleAction DecideSchedule(
            bool reconnectAlreadyInProgress,
            bool suppressReconnect,
            bool authRegistered)
        {
            if (reconnectAlreadyInProgress)
            {
                return CatchUpContinueScheduleAction.FinishBusy;
            }

            if (suppressReconnect)
            {
                return CatchUpContinueScheduleAction.FinishSuppress;
            }

            if (!authRegistered)
            {
                return CatchUpContinueScheduleAction.FinishUnregistered;
            }

            return CatchUpContinueScheduleAction.StartReconnect;
        }

        /// <summary>
        /// True when the cooldown window has not elapsed — host still starts reconnect,
        /// but may log a delay note.
        /// </summary>
        public static bool IsWithinCooldown(
            DateTime lastContinueUtc,
            DateTime nowUtc,
            TimeSpan cooldown) =>
            lastContinueUtc != DateTime.MinValue &&
            nowUtc - lastContinueUtc < cooldown;

        /// <summary>
        /// After the delay: abort when suppressed, unregistered, or tip looks fresh
        /// while catch-up is already idle (mid catch-up must not abort on tip freshness).
        /// </summary>
        public static bool ShouldAbortAfterDelay(
            bool suppressReconnect,
            bool authRegistered,
            bool tipLooksFresh,
            bool catchUpActive) =>
            suppressReconnect ||
            !authRegistered ||
            (tipLooksFresh && !catchUpActive);

        public static string AbortFinishReason(
            bool suppressReconnect,
            bool authRegistered,
            bool tipLooksFresh,
            bool catchUpActive)
        {
            if (suppressReconnect || !authRegistered)
            {
                return "continue-aborted";
            }

            if (tipLooksFresh && !catchUpActive)
            {
                return "fresh-before-continue";
            }

            return "continue-aborted";
        }
    }
}
