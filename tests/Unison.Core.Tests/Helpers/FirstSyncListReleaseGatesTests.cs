using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class FirstSyncListReleaseGatesTests
    {
        [Fact]
        public void No_names_pass_completes_now()
        {
            Assert.Equal(
                FirstSyncListReleaseAction.CompleteNow,
                FirstSyncListReleaseGates.Decide(false, false));
        }

        [Fact]
        public void Names_pass_already_in_flight_skips()
        {
            Assert.Equal(
                FirstSyncListReleaseAction.SkipAlreadyInFlight,
                FirstSyncListReleaseGates.Decide(true, true));
        }

        [Fact]
        public void Names_pass_unlocks_then_schedules_names()
        {
            Assert.Equal(
                FirstSyncListReleaseAction.UnlockThenScheduleNames,
                FirstSyncListReleaseGates.Decide(true, false));
        }
    }
}
