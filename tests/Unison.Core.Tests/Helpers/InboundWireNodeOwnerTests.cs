using System.Collections.Generic;
using Unison.Baileys.Protocol;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class InboundWireNodeOwnerTests
    {
        [Fact]
        public void Handle_ack_forwards_attrs_to_port()
        {
            string seenClass = null;
            string seenId = null;
            var owner = new InboundWireNodeOwner(
                (ackClass, ackId, ackError) =>
                {
                    seenClass = ackClass;
                    seenId = ackId;
                },
                (from, notify, participant) => { });

            owner.Handle(new BinaryNode
            {
                Tag = "ack",
                Attrs = new Dictionary<string, string>
                {
                    { "class", "message" },
                    { "id", "ABC" }
                }
            });

            Assert.Equal("message", seenClass);
            Assert.Equal("ABC", seenId);
        }
    }
}
