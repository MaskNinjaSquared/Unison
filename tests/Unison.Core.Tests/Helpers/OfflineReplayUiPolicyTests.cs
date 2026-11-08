using Unison.Core.Helpers.Chats;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class OfflineReplayUiPolicyTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, true)]
        public void SkipCatalogueReposition_only_when_frugal(bool preferFrugal, bool expectSkip)
        {
            Assert.Equal(expectSkip, OfflineReplayUiPolicy.SkipCatalogueReposition(preferFrugal));
        }

        [Theory]
        [InlineData(false, "replay-progressive", false)]
        [InlineData(true, "replay-progressive", true)]
        [InlineData(true, "offline-complete:0", false)]
        [InlineData(true, "settle", false)]
        public void SkipProgressiveTipHeal_only_frugal_progressive(
            bool preferFrugal,
            string reason,
            bool expectSkip)
        {
            Assert.Equal(
                expectSkip,
                OfflineReplayUiPolicy.SkipProgressiveTipHeal(preferFrugal, reason));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, true)]
        public void DeferTipPaintUntilSettle_only_when_frugal(bool preferFrugal, bool expectDefer)
        {
            Assert.Equal(
                expectDefer,
                OfflineReplayUiPolicy.DeferTipPaintUntilSettle(preferFrugal));
        }
    }
}
