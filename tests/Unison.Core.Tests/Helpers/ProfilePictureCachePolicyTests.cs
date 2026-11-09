using Unison.Core.Helpers;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ProfilePictureCachePolicyTests
    {
        [Fact]
        public void A_picture_is_kept_for_the_long_ttl()
        {
            Assert.Equal(ProfilePictureCachePolicy.FoundTtl, ProfilePictureCachePolicy.TtlFor(hasUrl: true, isNotFound: false));
        }

        [Fact]
        public void No_picture_is_kept_for_the_short_ttl()
        {
            Assert.Equal(ProfilePictureCachePolicy.NotFoundTtl, ProfilePictureCachePolicy.TtlFor(hasUrl: false, isNotFound: true));
            Assert.True(ProfilePictureCachePolicy.NotFoundTtl < ProfilePictureCachePolicy.FoundTtl);
        }

        [Fact]
        public void Timeouts_and_errors_are_never_kept()
        {
            Assert.Null(ProfilePictureCachePolicy.TtlFor(hasUrl: false, isNotFound: false));
        }

        [Fact]
        public void Chat_open_revalidates_on_every_device()
        {
            Assert.True(MobileChatListAvatarGate.AllowRefreshOnChatOpen(isMobile: false, chatChanged: true, hasChat: true));
            Assert.True(MobileChatListAvatarGate.AllowRefreshOnChatOpen(isMobile: true, chatChanged: true, hasChat: true));
        }
    }
}
