// =============================================================================
// MobileChatListAvatarGate — list scroll stays offline; open revalidates.
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class MobileChatListAvatarGateTests
    {
        [Fact]
        public void Mobile_does_not_refresh_on_row_realize()
        {
            Assert.False(MobileChatListAvatarGate.AllowRefreshOnRowRealized(isMobile: true));
        }

        [Fact]
        public void Desktop_may_refresh_on_row_realize()
        {
            Assert.True(MobileChatListAvatarGate.AllowRefreshOnRowRealized(isMobile: false));
        }

        [Fact]
        public void Mobile_refreshes_when_opening_another_chat()
        {
            Assert.True(MobileChatListAvatarGate.AllowRefreshOnChatOpen(
                isMobile: true,
                chatChanged: true,
                hasChat: true));
        }

        [Fact]
        public void Mobile_skips_refresh_when_re_binding_same_chat()
        {
            Assert.False(MobileChatListAvatarGate.AllowRefreshOnChatOpen(
                isMobile: true,
                chatChanged: false,
                hasChat: true));
        }

        [Fact]
        public void Desktop_also_revalidates_when_opening_another_chat()
        {
            Assert.True(MobileChatListAvatarGate.AllowRefreshOnChatOpen(
                isMobile: false,
                chatChanged: true,
                hasChat: true));
        }
    }
}
