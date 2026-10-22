// =============================================================================
// Tests for HistoryHydrationFallbackDecision.
// =============================================================================
using System;
using Unison.Core.Helpers;
using Xunit;
using Unison.WhatsAppClient.Helpers;

namespace Unison.Core.Tests.Helpers
{
    public class HistoryHydrationFallbackDecisionTests
    {
        [Fact]
        public void After_full_finish_stale_starts_per_chat()
        {
            Assert.Equal(
                HistoryHydrationFallbackAction.StartPerChatHydration,
                HistoryHydrationFallbackDecision.AfterFullCatchUpFinished(
                    stillStale: true,
                    hydrationAlreadyActive: false));
        }

        [Fact]
        public void After_full_finish_fresh_does_nothing()
        {
            Assert.Equal(
                HistoryHydrationFallbackAction.None,
                HistoryHydrationFallbackDecision.AfterFullCatchUpFinished(
                    stillStale: false,
                    hydrationAlreadyActive: false));
        }

        [Fact]
        public void After_full_finish_skips_when_hydration_active()
        {
            Assert.Equal(
                HistoryHydrationFallbackAction.None,
                HistoryHydrationFallbackDecision.AfterFullCatchUpFinished(
                    stillStale: true,
                    hydrationAlreadyActive: true));
        }

        [Fact]
        public void After_per_chat_still_stale_retries_full()
        {
            Assert.Equal(
                HistoryHydrationFallbackAction.RetryFullHistory,
                HistoryHydrationFallbackDecision.AfterPerChatHydration(stillStale: true));
        }

        [Fact]
        public void After_per_chat_fresh_stops()
        {
            Assert.Equal(
                HistoryHydrationFallbackAction.None,
                HistoryHydrationFallbackDecision.AfterPerChatHydration(stillStale: false));
        }

        [Fact]
        public void Day_retry_when_stale_budget_and_interval_ok()
        {
            Assert.Equal(
                HistoryHydrationFallbackAction.ScheduleDayRetry,
                HistoryHydrationFallbackDecision.DecideDayRetry(
                    stillStale: true,
                    attemptsToday: 1,
                    maxDayAttempts: 5,
                    elapsedSinceLastAttempt: TimeSpan.FromHours(3),
                    dayRetryInterval: TimeSpan.FromHours(3)));
        }

        [Fact]
        public void Day_retry_blocked_by_interval()
        {
            Assert.Equal(
                HistoryHydrationFallbackAction.None,
                HistoryHydrationFallbackDecision.DecideDayRetry(
                    stillStale: true,
                    attemptsToday: 1,
                    maxDayAttempts: 5,
                    elapsedSinceLastAttempt: TimeSpan.FromHours(1),
                    dayRetryInterval: TimeSpan.FromHours(3)));
        }

        [Fact]
        public void Day_retry_blocked_at_max_attempts()
        {
            Assert.Equal(
                HistoryHydrationFallbackAction.None,
                HistoryHydrationFallbackDecision.DecideDayRetry(
                    stillStale: true,
                    attemptsToday: 5,
                    maxDayAttempts: 5,
                    elapsedSinceLastAttempt: TimeSpan.FromHours(6),
                    dayRetryInterval: TimeSpan.FromHours(3)));
        }
    }
}
