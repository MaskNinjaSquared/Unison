using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class MissingMessagePriorityTests
    {
        [Fact]
        public void Own_messages_are_high_priority()
        {
            Assert.True(MissingMessagePriority.IsPeerOrSelf(true, "120363000000000000@g.us"));
        }

        [Fact]
        public void Direct_phone_and_lid_chats_are_high_priority()
        {
            Assert.True(MissingMessagePriority.IsPeerOrSelf(false, "5511999999999@s.whatsapp.net"));
            Assert.True(MissingMessagePriority.IsPeerOrSelf(false, "123456789012345@lid"));
        }

        [Fact]
        public void A_group_placeholder_from_someone_else_is_not()
        {
            Assert.False(MissingMessagePriority.IsPeerOrSelf(false, "120363000000000000@g.us"));
        }
    }
}
