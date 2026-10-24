using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class FirstSyncListReleaseGatesTests
    {
        [Fact]
        public void Completes_now_when_names_not_required()
        {
            Assert.Equal(
                FirstSyncListReleaseAction.CompleteNow,
                FirstSyncListReleaseGates.Decide(false, false));
        }

        [Fact]
        public void Skips_when_already_in_flight()
        {
            Assert.Equal(
                FirstSyncListReleaseAction.SkipAlreadyInFlight,
                FirstSyncListReleaseGates.Decide(true, true));
        }

        [Fact]
        public void Starts_names_pass()
        {
            Assert.Equal(
                FirstSyncListReleaseAction.StartNamesThenUnlock,
                FirstSyncListReleaseGates.Decide(true, false));
        }
    }
}
