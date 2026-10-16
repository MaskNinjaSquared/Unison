using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class MessageRevocationContentTests
    {
        [Fact]
        public void Tombstone_flags_the_message_and_clears_body_and_media()
        {
            var message = new ChatMessage
            {
                Content = "photo",
                Caption = "cap",
                Kind = ChatMessageKind.Image,
                IsImage = true,
                ImageUri = "file://x",
                ImageUrl = "https://x",
                IsAudio = true,
                AudioUri = "file://a"
            };

            MessageRevocationContent.ApplyTombstone(message);

            Assert.True(message.IsRevoked);
            Assert.Equal(string.Empty, message.Content);
            Assert.Equal(string.Empty, message.Caption);
            Assert.Equal(ChatMessageKind.Text, message.Kind);
            Assert.False(message.IsImage);
            Assert.Null(message.ImageUri);
            Assert.Null(message.ImageUrl);
            Assert.False(message.IsAudio);
            Assert.Null(message.AudioUri);
        }
    }
}
