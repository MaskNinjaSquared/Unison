using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class GroupHighAvatarTests
    {
        [Fact]
        public void A_group_without_a_high_picture_needs_one()
        {
            Assert.True(GroupHighAvatar.ShouldFetch(true, "1203@g.us", null));
            Assert.True(GroupHighAvatar.ShouldFetch(true, "1203@g.us", "  "));
        }

        [Fact]
        public void A_direct_chat_and_a_group_that_already_has_one_do_not()
        {
            Assert.False(GroupHighAvatar.ShouldFetch(false, "111@s.whatsapp.net", null));
            Assert.False(GroupHighAvatar.ShouldFetch(true, "1203@g.us", "ms-appdata:///local/a_high.jpg"));
            Assert.False(GroupHighAvatar.ShouldFetch(true, " ", null));
            Assert.False(GroupHighAvatar.ShouldFetch(true, null, null));
        }
    }
}
