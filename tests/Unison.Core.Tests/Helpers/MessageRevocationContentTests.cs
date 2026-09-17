using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class MessageRevocationContentTests
    {
        [Fact]
        public void Tombstone_clears_media_and_sets_deleted_label()
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

            Assert.Equal(MessageRevocationContent.DeletedLabel, message.Content);
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
