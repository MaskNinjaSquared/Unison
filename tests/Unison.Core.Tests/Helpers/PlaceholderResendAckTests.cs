using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class PlaceholderResendAckTests
    {
        [Fact]
        public void Only_a_message_ack_with_an_id_counts_and_an_error_is_a_refusal()
        {
            bool rejected;
            Assert.False(PlaceholderResendAck.TryRead("receipt", "id", null, out rejected));
            Assert.False(PlaceholderResendAck.TryRead("message", " ", null, out rejected));
            Assert.True(PlaceholderResendAck.TryRead("MESSAGE", "id", null, out rejected));
            Assert.False(rejected);
            Assert.True(PlaceholderResendAck.TryRead("message", "id", "479", out rejected));
            Assert.True(rejected);
        }
    }
}
