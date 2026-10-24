using System.Collections.Generic;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class InboundWireNodeDispatchTests
    {
        [Fact]
        public void Ack_notifies_wire_ack_and_message_node()
        {
            var host = new FakeHost();
            var attrs = new Dictionary<string, string>
            {
                ["class"] = "message",
                ["id"] = "ABC",
                ["error"] = "0",
                ["from"] = "1@s.whatsapp.net",
                ["notify"] = "Alice",
                ["participant"] = "2@lid"
            };

            InboundWireNodeDispatch.HandleAttrs(host, "ack", attrs);

            Assert.Equal(
                new[]
                {
                    "ack:message|ABC|0",
                    "node:1@s.whatsapp.net|Alice|2@lid"
                },
                host.Log);
        }

        [Fact]
        public void Non_ack_skips_wire_ack_but_notifies_message_node()
        {
            var host = new FakeHost();
            var attrs = new Dictionary<string, string>
            {
                ["from"] = "1@s.whatsapp.net",
                ["notify"] = "Bob"
            };

            InboundWireNodeDispatch.HandleAttrs(host, "message", attrs);

            Assert.Equal(
                new[] { "node:1@s.whatsapp.net|Bob|" },
                host.Log);
        }

        [Fact]
        public void Null_node_is_noop()
        {
            var host = new FakeHost();
            InboundWireNodeDispatch.Handle(host, null);
            Assert.Empty(host.Log);
        }

        private sealed class FakeHost : IInboundWireNodeHost
        {
            public List<string> Log { get; } = new List<string>();

            public void NotifyInboundWireAck(string ackClass, string ackId, string ackError) =>
                Log.Add("ack:" + ackClass + "|" + ackId + "|" + ackError);

            public void NotifyInboundMessageNode(
                string fromJid,
                string notify,
                string participantJid) =>
                Log.Add(
                    "node:" + (fromJid ?? string.Empty) + "|" +
                    (notify ?? string.Empty) + "|" +
                    (participantJid ?? string.Empty));
        }
    }
}
