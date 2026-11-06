using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Models
{
    public sealed class ChatMessageChannelPresentationTests
    {
        [Fact]
        public void Channel_posts_always_show_the_single_sent_tick()
        {
            var message = new ChatMessage
            {
                IsFromMe = true,
                RemoteJid = "120363@newsletter",
                Status = ChatMessage.StatusRead
            };

            Assert.Equal("\uEA03", message.StatusGlyph);
            Assert.False(message.IsReadStatus);
            Assert.True(message.ShowUnreadStatusCheckmark);
        }

        [Fact]
        public void View_count_shows_only_for_channel_posts_with_a_positive_count()
        {
            var message = new ChatMessage
            {
                RemoteJid = "120363@newsletter",
                ViewCount = 128
            };

            Assert.True(message.ShowViewCount);
            Assert.Equal("128", message.ViewCountText);

            message.ViewCount = 0;
            Assert.False(message.ShowViewCount);
        }
    }
}
