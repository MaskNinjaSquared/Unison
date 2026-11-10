using Unison.Core.Helpers.Chats;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ChatListUnloadPolicyTests
    {
        [Theory]
        [InlineData(0, false)]
        [InlineData(1, true)]
        [InlineData(257, true)]
        public void ShouldClearVisibleChats_when_any_painted_row(int count, bool expect)
        {
            Assert.Equal(expect, ChatListUnloadPolicy.ShouldClearVisibleChats(count));
        }
    }
}
