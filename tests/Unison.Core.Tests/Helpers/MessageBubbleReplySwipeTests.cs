using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class MessageBubbleReplySwipeTests
    {
        [Theory]
        [InlineData(-72, 0)]
        [InlineData(-96, 24)]
        [InlineData(-180, -40)]
        public void A_deliberate_left_drag_opens_reply(double horizontal, double vertical)
        {
            Assert.True(MessageBubbleReplySwipe.ShouldReply(horizontal, vertical));
        }

        [Theory]
        [InlineData(-71, 0)]
        [InlineData(72, 0)]
        [InlineData(0, 72)]
        [InlineData(-72, 72)]
        [InlineData(-96, 120)]
        public void A_short_rightward_or_vertical_drag_does_not_open_reply(double horizontal, double vertical)
        {
            Assert.False(MessageBubbleReplySwipe.ShouldReply(horizontal, vertical));
        }
    }
}
