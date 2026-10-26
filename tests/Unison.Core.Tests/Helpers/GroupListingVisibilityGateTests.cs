using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class GroupListingVisibilityGateTests
    {
        [Fact]
        public void Defer_start_when_window_hidden()
        {
            Assert.True(GroupListingVisibilityGate.ShouldDeferStart(isWindowVisible: false));
            Assert.False(GroupListingVisibilityGate.ShouldDeferStart(isWindowVisible: true));
        }

        [Fact]
        public void Abort_in_flight_when_hidden_or_flagged()
        {
            Assert.True(GroupListingVisibilityGate.ShouldAbortInFlight(
                isWindowVisible: false,
                abortRequested: false));
            Assert.True(GroupListingVisibilityGate.ShouldAbortInFlight(
                isWindowVisible: true,
                abortRequested: true));
            Assert.False(GroupListingVisibilityGate.ShouldAbortInFlight(
                isWindowVisible: true,
                abortRequested: false));
        }

        [Fact]
        public void Resume_retry_only_when_settle_generation_still_current()
        {
            Assert.True(GroupListingVisibilityGate.ShouldFireResumeRetry(
                isWindowVisible: true,
                listingRetryPending: true,
                armedGeneration: 3,
                currentGeneration: 3));

            Assert.False(GroupListingVisibilityGate.ShouldFireResumeRetry(
                isWindowVisible: true,
                listingRetryPending: true,
                armedGeneration: 3,
                currentGeneration: 4));

            Assert.False(GroupListingVisibilityGate.ShouldFireResumeRetry(
                isWindowVisible: false,
                listingRetryPending: true,
                armedGeneration: 3,
                currentGeneration: 3));

            Assert.False(GroupListingVisibilityGate.ShouldFireResumeRetry(
                isWindowVisible: true,
                listingRetryPending: false,
                armedGeneration: 3,
                currentGeneration: 3));
        }
    }
}
