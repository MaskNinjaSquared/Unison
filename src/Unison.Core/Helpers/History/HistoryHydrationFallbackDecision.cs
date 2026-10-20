// =============================================================================
// HistoryHydrationFallbackDecision
//
// When FULL_HISTORY catch-up finishes empty/stagnant and tips are still stale,
// hydrate per-chat HISTORY_SYNC_ON_DEMAND (Pending → InFlight → HasMore/Synced),
// then solicit FULL_HISTORY again. Day pacing keeps trying until tips look fresh
// without burning the phone on every minute.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public enum HistoryHydrationFallbackAction
    {
        None,
        StartPerChatHydration,
        RetryFullHistory,
        ScheduleDayRetry
    }

    public static class HistoryHydrationFallbackDecision
    {
        public static readonly TimeSpan DefaultDayRetryInterval = TimeSpan.FromHours(3);

        public const int DefaultMaxDayAttempts = 5;

        /// <summary>
        /// After FULL_HISTORY quiet finish: start per-chat hydration when tips still look owed.
        /// </summary>
        public static HistoryHydrationFallbackAction AfterFullCatchUpFinished(
            bool stillStale,
            bool hydrationAlreadyActive)
        {
            if (!stillStale || hydrationAlreadyActive)
            {
                return HistoryHydrationFallbackAction.None;
            }

            return HistoryHydrationFallbackAction.StartPerChatHydration;
        }

        /// <summary>
        /// After the per-chat pass settled: ask FULL_HISTORY again when tips still look owed.
        /// </summary>
        public static HistoryHydrationFallbackAction AfterPerChatHydration(bool stillStale)
        {
            return stillStale
                ? HistoryHydrationFallbackAction.RetryFullHistory
                : HistoryHydrationFallbackAction.None;
        }

        /// <summary>
        /// When FULL is on cooldown / just failed again: another day attempt if budget remains.
        /// </summary>
        public static HistoryHydrationFallbackAction DecideDayRetry(
            bool stillStale,
            int attemptsToday,
            int maxDayAttempts,
            TimeSpan elapsedSinceLastAttempt,
            TimeSpan dayRetryInterval)
        {
            if (!stillStale)
            {
                return HistoryHydrationFallbackAction.None;
            }

            if (maxDayAttempts < 1 || attemptsToday >= maxDayAttempts)
            {
                return HistoryHydrationFallbackAction.None;
            }

            if (elapsedSinceLastAttempt < dayRetryInterval)
            {
                return HistoryHydrationFallbackAction.None;
            }

            return HistoryHydrationFallbackAction.ScheduleDayRetry;
        }
    }
}
