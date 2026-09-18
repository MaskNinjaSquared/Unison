// =============================================================================
// Tests for ReceiptTypeStatusMap.
// =============================================================================
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ReceiptTypeStatusMapTests
    {
        [Fact]
        public void Blank_type_means_delivered()
        {
            Assert.Equal(ChatMessage.StatusDelivered, ReceiptTypeStatusMap.FromReceiptType(null));
            Assert.Equal(ChatMessage.StatusDelivered, ReceiptTypeStatusMap.FromReceiptType(""));
            Assert.Equal(ChatMessage.StatusDelivered, ReceiptTypeStatusMap.FromReceiptType("  "));
        }

        [Theory]
        [InlineData("sender", ChatMessage.StatusSent)]
        [InlineData("read", ChatMessage.StatusRead)]
        [InlineData("read-self", ChatMessage.StatusRead)]
        [InlineData("played", ChatMessage.StatusRead)]
        [InlineData("played-self", ChatMessage.StatusRead)]
        [InlineData("delivery", ChatMessage.StatusDelivered)]
        [InlineData("delivered", ChatMessage.StatusDelivered)]
        public void Known_types_map(string type, string expected)
        {
            Assert.Equal(expected, ReceiptTypeStatusMap.FromReceiptType(type));
        }

        [Fact]
        public void Unknown_type_returns_null()
        {
            Assert.Null(ReceiptTypeStatusMap.FromReceiptType("retry"));
            Assert.Null(ReceiptTypeStatusMap.FromReceiptType("inactive"));
        }
    }
}
