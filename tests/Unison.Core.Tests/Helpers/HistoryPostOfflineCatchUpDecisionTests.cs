// =============================================================================
// Tests for HistoryPostOfflineCatchUpDecision — any post-offline freshness stale
// solicits FULL_HISTORY (no mild-age skip).
// =============================================================================
using System;
using Unison.Core.Helpers;
using Xunit;
using Unison.WhatsAppClient.Helpers;

namespace Unison.Core.Tests.Helpers
{
    public class HistoryPostOfflineCatchUpDecisionTests
    {
        [Fact]
        public void Fresh_tips_never_request()
        {
            Assert.False(HistoryPostOfflineCatchUpDecision.ShouldRequestFullHistory(
                offlineCount: 0,
                stillStale: false,
                staleReason: null));
        }

        [Fact]
        public void Empty_offline_with_mild_age_stale_requests()
        {
            Assert.True(HistoryPostOfflineCatchUpDecision.ShouldRequestFullHistory(
                offlineCount: 0,
                stillStale: true,
                staleReason: "non-self-stale:2026-10-19T13:36:39.0000000Z:ageMinutes=64.9:newestAny=2026-10-19T13:36:39.0000000Z"));
        }

        [Fact]
        public void Empty_offline_with_mild_newest_stale_requests()
        {
            Assert.True(HistoryPostOfflineCatchUpDecision.ShouldRequestFullHistory(
                offlineCount: 0,
                stillStale: true,
                staleReason: "newest-stale:2026-10-19T13:36:39.0000000Z:ageMinutes=64.9"));
        }

        [Fact]
        public void Empty_offline_with_severe_age_stale_requests()
        {
            Assert.True(HistoryPostOfflineCatchUpDecision.ShouldRequestFullHistory(
                offlineCount: 0,
                stillStale: true,
                staleReason: "non-self-stale:2026-10-12T21:53:49.0000000Z:ageMinutes=900:newestAny=2026-10-12T21:53:49.0000000Z"));
        }

        [Fact]
        public void Empty_offline_with_severe_group_stale_requests()
        {
            Assert.True(HistoryPostOfflineCatchUpDecision.ShouldRequestFullHistory(
                offlineCount: 0,
                stillStale: true,
                staleReason: "group-stale:2026-10-12T21:53:49.0000000Z:ageMinutes=900"));
        }

        [Fact]
        public void Explicit_tip_age_while_stale_requests()
        {
            Assert.True(HistoryPostOfflineCatchUpDecision.ShouldRequestFullHistory(
                offlineCount: 0,
                stillStale: true,
                staleReason: "non-self-stale:opaque",
                tipAge: TimeSpan.FromMinutes(45)));
        }

        [Fact]
        public void Unreadable_age_while_still_stale_requests()
        {
            Assert.True(HistoryPostOfflineCatchUpDecision.ShouldRequestFullHistory(
                offlineCount: 0,
                stillStale: true,
                staleReason: "non-self-stale:2026-10-12T21:53:49.0000000Z:ageMinutes="));
        }

        [Theory]
        [InlineData("no-stored-messages")]
        [InlineData("no-non-self-messages:newestAny=2026-10-19T13:36:39.0000000Z")]
        [InlineData("no-group-messages:newestAny=x:newestNonSelf=y")]
        public void Empty_offline_with_structural_gap_still_requests(string reason)
        {
            Assert.True(HistoryPostOfflineCatchUpDecision.ShouldRequestFullHistory(
                offlineCount: 0,
                stillStale: true,
                staleReason: reason));
        }

        [Fact]
        public void Non_empty_offline_while_stale_requests()
        {
            Assert.True(HistoryPostOfflineCatchUpDecision.ShouldRequestFullHistory(
                offlineCount: 3,
                stillStale: true,
                staleReason: "non-self-stale:…:ageMinutes=64.9"));
        }

        [Fact]
        public void TryGetAgeMinutes_reads_invariant_float()
        {
            Assert.True(HistoryPostOfflineCatchUpDecision.TryGetAgeMinutes(
                "non-self-stale:x:ageMinutes=64.9:newestAny=y",
                out double minutes));
            Assert.Equal(64.9, minutes, 3);
        }

        [Fact]
        public void TryGetAgeMinutes_accepts_comma_decimal_from_legacy_reasons()
        {
            Assert.True(HistoryPostOfflineCatchUpDecision.TryGetAgeMinutes(
                "non-self-stale:x:ageMinutes=10080,5:newestAny=y",
                out double minutes));
            Assert.Equal(10080.5, minutes, 3);
        }
    }
}
