using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class IncomingSelfChatCollapseGateTests
    {
        [Fact]
        public void Self_chat_with_distinct_peer_lid_collapses()
        {
            Assert.True(IncomingSelfChatCollapseGate.ShouldCollapse(
                "self-chat",
                "123456789012345@lid",
                "5511999999999@s.whatsapp.net"));
        }

        [Fact]
        public void Matching_lid_does_not_collapse()
        {
            Assert.False(IncomingSelfChatCollapseGate.ShouldCollapse(
                "self-chat",
                "123456789012345@lid",
                "123456789012345@lid"));
        }

        [Fact]
        public void Other_routing_reasons_do_not_collapse()
        {
            Assert.False(IncomingSelfChatCollapseGate.ShouldCollapse(
                "peer-recipient-pn",
                "123456789012345@lid",
                "5511999999999@s.whatsapp.net"));
        }
    }
}
