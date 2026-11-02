using Unison.Core.Helpers.Calls;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class CallPeerJidTests
    {
        [Fact]
        public void Resolve_prefers_from_over_call_relay_chatId()
        {
            string jid = CallPeerJid.Resolve(
                chatId: "abc@call",
                from: "5511999999999@s.whatsapp.net",
                groupJid: null,
                isGroup: false);

            Assert.Equal("5511999999999@s.whatsapp.net", jid);
        }

        [Fact]
        public void Resolve_uses_group_jid_for_group_calls()
        {
            string jid = CallPeerJid.Resolve(
                chatId: "abc@call",
                from: "5511999999999@s.whatsapp.net",
                groupJid: "120363@g.us",
                isGroup: true);

            Assert.Equal("120363@g.us", jid);
        }

        [Fact]
        public void Resolve_falls_back_to_chatId_when_from_is_relay()
        {
            string jid = CallPeerJid.Resolve(
                chatId: "5511888888888@s.whatsapp.net",
                from: "xyz@call",
                groupJid: null,
                isGroup: false);

            Assert.Equal("5511888888888@s.whatsapp.net", jid);
        }
    }
}
