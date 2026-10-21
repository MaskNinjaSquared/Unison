// =============================================================================
// Tests for LaunchActivationHelper (chat= vs profile= deep links).
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class LaunchActivationHelperTests
    {
        [Fact]
        public void Chat_argument_returns_the_jid()
        {
            string jid = LaunchActivationHelper.TryGetChatJid(
                "notification=1&chat=5511999990001%40s.whatsapp.net");

            Assert.Equal("5511999990001@s.whatsapp.net", jid);
        }

        [Fact]
        public void Profile_argument_returns_the_jid()
        {
            string jid = LaunchActivationHelper.TryGetProfileJid(
                "profile=5511999990001%40s.whatsapp.net");

            Assert.Equal("5511999990001@s.whatsapp.net", jid);
        }

        [Fact]
        public void Profile_key_is_not_read_as_a_chat_open()
        {
            Assert.Null(LaunchActivationHelper.TryGetChatJid("profile=5511999990001@s.whatsapp.net"));
        }

        [Fact]
        public void Chat_key_is_not_read_as_a_profile_open()
        {
            Assert.Null(LaunchActivationHelper.TryGetProfileJid("chat=5511999990001@s.whatsapp.net"));
        }
    }
}
