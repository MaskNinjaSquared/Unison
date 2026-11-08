using Unison.Core.Helpers.Chats;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class LiveIncomingStripUiPolicyTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, true)]
        public void SkipCatalogueReposition_only_when_frugal(bool preferFrugal, bool expectSkip)
        {
            Assert.Equal(expectSkip, LiveIncomingStripUiPolicy.SkipCatalogueReposition(preferFrugal));
        }
    }
}
