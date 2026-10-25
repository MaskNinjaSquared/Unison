using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ReleaseEnrichmentAfterHeavyHistoryCoordinatorTests
    {
        [Fact]
        public void Run_defers_and_sets_awaiting_when_catch_up_active()
        {
            string scheduled = null;
            var coord = new ReleaseEnrichmentAfterHeavyHistoryCoordinator(
                () => true,
                reason => scheduled = reason);

            coord.Run("sqlite");

            Assert.True(coord.IsAwaitingHeavyHistory);
            Assert.Null(scheduled);
        }

        [Fact]
        public void Run_clears_awaiting_and_schedules_when_idle()
        {
            string scheduled = null;
            var coord = new ReleaseEnrichmentAfterHeavyHistoryCoordinator(
                () => false,
                reason => scheduled = reason);
            coord.SetAwaitingHeavyHistory(true);

            coord.Run("sqlite");

            Assert.False(coord.IsAwaitingHeavyHistory);
            Assert.Equal("heavy-history:sqlite", scheduled);
        }

        [Fact]
        public void SetAwaitingHeavyHistory_toggles_flag()
        {
            var coord = new ReleaseEnrichmentAfterHeavyHistoryCoordinator(
                () => false,
                _ => { });

            Assert.False(coord.IsAwaitingHeavyHistory);
            coord.SetAwaitingHeavyHistory(true);
            Assert.True(coord.IsAwaitingHeavyHistory);
            coord.SetAwaitingHeavyHistory(false);
            Assert.False(coord.IsAwaitingHeavyHistory);
        }
    }
}
