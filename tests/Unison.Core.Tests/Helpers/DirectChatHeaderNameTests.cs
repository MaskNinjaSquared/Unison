using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class DirectChatHeaderNameTests
    {
        private const string Jid = "15551234567@s.whatsapp.net";

        [Fact]
        public void Keeps_chat_name_when_resolver_only_knows_the_number()
        {
            Assert.Equal("Lindsay Hefner", DirectChatHeaderName.Pick("+1 555 123 4567", "Lindsay Hefner", Jid));
            Assert.Equal("Lindsay Hefner", DirectChatHeaderName.Pick("15551234567", "Lindsay Hefner", Jid));
        }

        [Fact]
        public void Resolver_name_wins_when_it_has_one()
        {
            Assert.Equal("Lindsay", DirectChatHeaderName.Pick("Lindsay", "Lindsay Hefner", Jid));
        }

        [Fact]
        public void Falls_back_to_resolver_when_neither_is_a_name()
        {
            Assert.Equal("+1 555 123 4567", DirectChatHeaderName.Pick("+1 555 123 4567", "15551234567", Jid));
        }
    }
}
