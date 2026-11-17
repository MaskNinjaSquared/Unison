using System.Collections.Generic;
using Unison.Baileys.Protocol;
using Unison.Socket.Models;
using Unison.Socket.Notifications;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class CallStatusReaderTests
    {
        private static BinaryNode Action(string tag, Dictionary<string, string> attrs = null) =>
            new BinaryNode(tag, attrs ?? new Dictionary<string, string>());

        [Fact]
        public void Live_offer_is_an_offer()
        {
            Assert.Equal(CallStatus.Offer, CallStatusReader.Read(Action("offer")));
        }

        [Fact]
        public void Offer_flagged_is_call_ended_is_a_terminate_not_a_ring()
        {
            var node = Action("offer", new Dictionary<string, string> { { "is_call_ended", "1" } });
            Assert.Equal(CallStatus.Terminate, CallStatusReader.Read(node));
        }

        [Fact]
        public void Offer_with_terminate_reason_is_a_terminate_not_a_ring()
        {
            var node = Action(
                "offer",
                new Dictionary<string, string> { { "terminate_reason", "accepted_elsewhere" } });
            Assert.Equal(CallStatus.Terminate, CallStatusReader.Read(node));
        }

        [Theory]
        [InlineData("timeout", CallStatus.Timeout)]
        [InlineData("rejected", CallStatus.Reject)]
        [InlineData("accept", CallStatus.Accept)]
        [InlineData("other", CallStatus.Terminate)]
        public void Terminate_reason_maps_status(string reason, CallStatus expected)
        {
            var node = Action("terminate", new Dictionary<string, string> { { "reason", reason } });
            Assert.Equal(expected, CallStatusReader.Read(node));
        }

        [Fact]
        public void Unknown_action_is_ringing()
        {
            Assert.Equal(CallStatus.Ringing, CallStatusReader.Read(Action("relaylatency")));
        }
    }
}
