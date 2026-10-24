// =============================================================================
// FreshnessReconnectFallbackGates
//
// Whether ScheduleFreshnessReconnectFallback should reconnect, hydrate on
// cooldown, or skip. Persist / Task.Run / socket tear-down stay on the host.
// =============================================================================
using System;

namespace Unison.WhatsAppClient.Helpers
{
    public enum FreshnessReconnectScheduleAction
    {
        SkipInProgress,
        SkipSuppress,
        SkipNotRegistered,
        /// <summary>Cooldown active — host runs hydration pipeline instead of reconnect.</summary>
        HydrateOnCooldown,
        SkipFresh,
        StartReconnect
    }

    public static class FreshnessReconnectFallbackGates
    {
        public static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(20);

        public static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(4);

        public static FreshnessReconnectScheduleAction DecideSchedule(
            bool alreadyInProgress,
            bool suppressReconnect,
            bool authRegistered,
            DateTime lastFallbackUtc,
            DateTime nowUtc,
            TimeSpan cooldown,
            bool tipsAreStale)
        {
            if (alreadyInProgress)
            {
                return FreshnessReconnectScheduleAction.SkipInProgress;
            }

            if (suppressReconnect)
            {
                return FreshnessReconnectScheduleAction.SkipSuppress;
            }

            if (!authRegistered)
            {
                return FreshnessReconnectScheduleAction.SkipNotRegistered;
            }

            if (lastFallbackUtc != DateTime.MinValue &&
                nowUtc - lastFallbackUtc < cooldown)
            {
                return FreshnessReconnectScheduleAction.HydrateOnCooldown;
            }

            if (!tipsAreStale)
            {
                return FreshnessReconnectScheduleAction.SkipFresh;
            }

            return FreshnessReconnectScheduleAction.StartReconnect;
        }

        public static bool ShouldAbortAfterDelay(
            bool suppressReconnect,
            bool authRegistered,
            bool tipsStillStale) =>
            suppressReconnect || !authRegistered || !tipsStillStale;
    }
}
