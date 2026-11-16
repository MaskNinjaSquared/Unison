using System.Collections.Generic;
using Unison.Baileys.Protocol;
using Unison.Socket.UseCases.Calls;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class CallStanzaBuildersTests
    {
        [Fact]
        public void BuildReject_wraps_reject_under_call_with_meowcaller_attrs()
        {
            BinaryNode node = CallStanzaBuilders.BuildReject(
                "ABC",
                "1555@s.whatsapp.net",
                "1555@s.whatsapp.net");

            Assert.Equal("call", node.Tag);
            Assert.Equal("1555@s.whatsapp.net", node.GetAttribute("to"));

            var children = node.Content as List<BinaryNode>;
            Assert.NotNull(children);
            Assert.Single(children);
            Assert.Equal("reject", children[0].Tag);
            Assert.Equal("ABC", children[0].GetAttribute("call-id"));
            Assert.Equal("1555@s.whatsapp.net", children[0].GetAttribute("call-creator"));
        }

        [Fact]
        public void BuildTerminate_includes_optional_reason()
        {
            BinaryNode node = CallStanzaBuilders.BuildTerminate(
                "XYZ",
                "1555@s.whatsapp.net",
                "1555@s.whatsapp.net",
                "timeout");

            var children = node.Content as List<BinaryNode>;
            Assert.NotNull(children);
            Assert.Equal("terminate", children[0].Tag);
            Assert.Equal("timeout", children[0].GetAttribute("reason"));
        }
    }
}
