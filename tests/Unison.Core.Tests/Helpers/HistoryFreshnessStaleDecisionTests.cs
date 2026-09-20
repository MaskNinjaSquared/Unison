// =============================================================================
// Tests for HistoryFreshnessStaleDecision — mirrors TryGetHistoryFreshnessStaleReason.
// =============================================================================
using System;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class HistoryFreshnessStaleDecisionTests
    {
        private static readonly DateTime Now =
            new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);

        private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(30);

        [Fact]
        public void No_messages_at_all_is_stale()
        {
            string reason;
            Assert.True(HistoryFreshnessStaleDecision.IsStale(
                Now,
                DateTime.MinValue,
                DateTime.MinValue,
                hasGroupChats: false,
                DateTime.MinValue,
                Threshold,
                out reason));
            Assert.Equal("no-stored-messages", reason);
        }

        [Fact]
        public void Only_self_messages_is_stale()
        {
            string reason;
            Assert.True(HistoryFreshnessStaleDecision.IsStale(
                Now,
                newestAnyUtc: Now.AddMinutes(-5),
                newestNonSelfUtc: DateTime.MinValue,
                hasGroupChats: false,
                newestGroupUtc: DateTime.MinValue,
                Threshold,
                out reason));
            Assert.StartsWith("no-non-self-messages:", reason);
        }

        [Fact]
        public void Non_self_older_than_threshold_is_stale()
        {
            string reason;
            Assert.True(HistoryFreshnessStaleDecision.IsStale(
                Now,
                newestAnyUtc: Now.AddMinutes(-5),
                newestNonSelfUtc: Now.AddMinutes(-45),
                hasGroupChats: false,
                newestGroupUtc: DateTime.MinValue,
                Threshold,
                out reason));
            Assert.StartsWith("non-self-stale:", reason);
        }

        [Fact]
        public void Groups_present_but_no_group_tip_is_stale()
        {
            string reason;
            Assert.True(HistoryFreshnessStaleDecision.IsStale(
                Now,
                newestAnyUtc: Now.AddMinutes(-5),
                newestNonSelfUtc: Now.AddMinutes(-5),
                hasGroupChats: true,
                newestGroupUtc: DateTime.MinValue,
                Threshold,
                out reason));
            Assert.StartsWith("no-group-messages:", reason);
        }

        [Fact]
        public void Group_tip_older_than_threshold_is_stale()
        {
            string reason;
            Assert.True(HistoryFreshnessStaleDecision.IsStale(
                Now,
                newestAnyUtc: Now.AddMinutes(-5),
                newestNonSelfUtc: Now.AddMinutes(-5),
                hasGroupChats: true,
                newestGroupUtc: Now.AddMinutes(-50),
                Threshold,
                out reason));
            Assert.StartsWith("group-stale:", reason);
        }

        [Fact]
        public void Newest_any_older_than_threshold_is_stale()
        {
            // Non-self and groups are fresh enough; overall newest is not (clock skew edge).
            string reason;
            Assert.True(HistoryFreshnessStaleDecision.IsStale(
                Now,
                newestAnyUtc: Now.AddMinutes(-40),
                newestNonSelfUtc: Now.AddMinutes(-10),
                hasGroupChats: false,
                newestGroupUtc: DateTime.MinValue,
                Threshold,
                out reason));
            Assert.StartsWith("newest-stale:", reason);
        }

        [Fact]
        public void Fresh_tips_are_not_stale()
        {
            string reason;
            Assert.False(HistoryFreshnessStaleDecision.IsStale(
                Now,
                newestAnyUtc: Now.AddMinutes(-5),
                newestNonSelfUtc: Now.AddMinutes(-5),
                hasGroupChats: true,
                newestGroupUtc: Now.AddMinutes(-8),
                Threshold,
                out reason));
            Assert.Null(reason);
        }

        [Fact]
        public void No_groups_skips_group_checks()
        {
            string reason;
            Assert.False(HistoryFreshnessStaleDecision.IsStale(
                Now,
                newestAnyUtc: Now.AddMinutes(-5),
                newestNonSelfUtc: Now.AddMinutes(-5),
                hasGroupChats: false,
                newestGroupUtc: DateTime.MinValue,
                Threshold,
                out reason));
            Assert.Null(reason);
        }
    }
}
