// =============================================================================
// Tests for WebMessageStatusMap.
// =============================================================================
using Proto;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class WebMessageStatusMapTests
    {
        [Fact]
        public void Null_or_missing_status_returns_null()
        {
            Assert.Null(WebMessageStatusMap.FromWebMessageInfo(null));
            Assert.Null(WebMessageStatusMap.FromWebMessageInfo(new WebMessageInfo()));
        }

        [Theory]
        [InlineData(WebMessageInfo.Types.Status.Error, ChatMessage.StatusFailed)]
        [InlineData(WebMessageInfo.Types.Status.Pending, ChatMessage.StatusPending)]
        [InlineData(WebMessageInfo.Types.Status.ServerAck, ChatMessage.StatusSent)]
        [InlineData(WebMessageInfo.Types.Status.DeliveryAck, ChatMessage.StatusDelivered)]
        [InlineData(WebMessageInfo.Types.Status.Read, ChatMessage.StatusRead)]
        [InlineData(WebMessageInfo.Types.Status.Played, ChatMessage.StatusRead)]
        public void Known_statuses_map_to_chat_message_strings(
            WebMessageInfo.Types.Status protoStatus,
            string expected)
        {
            var info = new WebMessageInfo { Status = protoStatus };
            Assert.Equal(expected, WebMessageStatusMap.FromWebMessageInfo(info));
        }
    }
}
