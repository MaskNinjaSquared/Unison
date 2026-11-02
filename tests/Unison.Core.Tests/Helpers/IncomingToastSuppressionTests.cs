// =============================================================================
// Tests for IncomingToastSuppression — when live/offline envelopes must stay quiet.
// =============================================================================
using System;
using Unison.Core.Helpers.Messages;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class IncomingToastSuppressionTests
    {
        private static readonly DateTime Now =
            new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void ShouldQuiet_false_for_fresh_live_message_outside_sync()
        {
            bool quiet = IncomingToastSuppression.ShouldQuiet(
                initialSyncActive: false,
                awaitingInitialSync: false,
                historyCatchUpQuiet: false,
                isOfflineEnvelope: false,
                messageTimestamp: Now.AddMinutes(-1),
                nowUtc: Now);

            Assert.False(quiet);
        }

        [Fact]
        public void ShouldQuiet_true_while_initial_sync_or_awaiting()
        {
            Assert.True(IncomingToastSuppression.ShouldQuiet(
                true, false, false, false, Now, Now));
            Assert.True(IncomingToastSuppression.ShouldQuiet(
                false, true, false, false, Now, Now));
        }

        [Fact]
        public void ShouldQuiet_true_while_history_catch_up_is_quiet()
        {
            Assert.True(IncomingToastSuppression.ShouldQuiet(
                false, false, true, false, Now, Now));
        }

        [Fact]
        public void ShouldQuiet_true_for_offline_envelope_even_when_fresh()
        {
            Assert.True(IncomingToastSuppression.ShouldQuiet(
                false, false, false, true, Now.AddSeconds(-5), Now));
        }

        [Fact]
        public void ShouldQuiet_true_when_message_older_than_freshness_window()
        {
            Assert.True(IncomingToastSuppression.ShouldQuiet(
                false,
                false,
                false,
                false,
                Now.AddMinutes(-6),
                Now,
                TimeSpan.FromMinutes(5)));
        }

        [Fact]
        public void ShouldQuiet_false_when_timestamp_unknown()
        {
            Assert.False(IncomingToastSuppression.ShouldQuiet(
                false, false, false, false, default(DateTime), Now));
        }
    }
}
