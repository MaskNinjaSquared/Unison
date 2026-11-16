using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class FirstSyncListHoldStateTests
    {
        [Theory]
        [InlineData(true, false, false, true)]
        [InlineData(false, true, false, true)]
        [InlineData(false, false, true, true)]
        [InlineData(false, false, false, false)]
        public void Hold_is_in_progress_while_any_owner_still_needs_it(
            bool initialSyncActive,
            bool releaseDeferred,
            bool holdOwned,
            bool expected)
        {
            var state = new FirstSyncListHoldState
            {
                ReleaseDeferred = releaseDeferred,
                HoldOwned = holdOwned
            };

            Assert.Equal(expected, state.IsInProgress(initialSyncActive));
        }

        [Fact]
        public void Already_released_wins_over_every_other_flag()
        {
            var state = new FirstSyncListHoldState
            {
                ReleaseDeferred = true,
                HoldOwned = true,
                AlreadyReleased = true
            };

            Assert.False(state.IsInProgress(initialSyncActive: true));
        }

        [Fact]
        public void Reset_clears_all_three_flags()
        {
            var state = new FirstSyncListHoldState
            {
                ReleaseDeferred = true,
                HoldOwned = true,
                AlreadyReleased = true
            };

            state.Reset();

            Assert.False(state.ReleaseDeferred);
            Assert.False(state.HoldOwned);
            Assert.False(state.AlreadyReleased);
        }
    }
}
