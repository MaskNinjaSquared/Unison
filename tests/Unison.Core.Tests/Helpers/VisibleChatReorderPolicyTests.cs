using Unison.Core.Helpers.Chats;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class VisibleChatReorderPolicyTests
    {
        [Theory]
        [InlineData(false, 300, false)]
        [InlineData(true, 48, false)]
        [InlineData(true, 49, true)]
        [InlineData(true, 257, true)]
        public void PreferAsyncDrain_only_frugal_above_sync_cap(
            bool preferFrugal,
            int visibleCount,
            bool expectAsync)
        {
            Assert.Equal(
                expectAsync,
                VisibleChatReorderPolicy.PreferAsyncDrain(preferFrugal, visibleCount));
        }
    }
}
