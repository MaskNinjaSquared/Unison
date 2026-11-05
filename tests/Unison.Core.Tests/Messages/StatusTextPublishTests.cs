using System.Threading.Tasks;
using Unison.Socket.Messages.Content;
using Unison.WhatsAppClient.Status;
using Xunit;

namespace Unison.Core.Tests.Messages
{
    public sealed class StatusTextPublishTests
    {
        [Fact]
        public async Task Status_text_builds_an_extended_text_with_colours_and_font()
        {
            var factory = new MessageFactory();

            BuiltMessage built = await factory.BuildAsync(new StatusTextContent("hello")
            {
                BackgroundArgb = 0xFF128C7E,
                TextArgb = 0xFFFFFFFF,
                Font = 10
            });

            var extended = built.Content.ExtendedTextMessage;
            Assert.NotNull(extended);
            Assert.Equal("hello", extended.Text);
            Assert.Equal(0xFF128C7Eu, extended.BackgroundArgb);
            Assert.Equal(0xFFFFFFFFu, extended.TextArgb);
            Assert.Equal(
                global::Proto.Message.Types.ExtendedTextMessage.Types.FontType.CourierprimeBold,
                extended.Font);
            Assert.Null(built.Attributes);
        }

        [Fact]
        public void Recipient_list_puts_self_first_dedupes_and_drops_non_users()
        {
            var list = StatusRecipientList.Build(
                new[]
                {
                    "1@s.whatsapp.net",
                    "1:5@s.whatsapp.net",
                    "2@lid",
                    "9@g.us",
                    "status@broadcast",
                    "me@s.whatsapp.net",
                    null
                },
                "me:3@s.whatsapp.net");

            Assert.Equal(new[] { "me@s.whatsapp.net", "1@s.whatsapp.net", "2@lid" }, list);
        }
    }
}
