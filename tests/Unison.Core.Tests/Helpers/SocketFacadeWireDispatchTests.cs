using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class SocketFacadeWireDispatchTests
    {
        [Fact]
        public void Presence_forwards_args()
        {
            var host = new FakeHost();
            var args = new PresenceUpdateEventArgs { Jid = "1@s.whatsapp.net" };

            SocketFacadeWireDispatch.HandlePresence(host, args);

            Assert.Equal(new[] { "presence:1@s.whatsapp.net" }, host.Log);
        }

        [Fact]
        public void Missing_message_forwards_primitives()
        {
            var host = new FakeHost();

            SocketFacadeWireDispatch.HandleMissingMessage(
                host,
                "c@g.us",
                "p@lid",
                "id1",
                isFromMe: false,
                timestamp: DateTime.UnixEpoch,
                reason: "cipher");

            Assert.Equal(
                new[] { "missing:c@g.us|id1|cipher" },
                host.Log);
        }

        [Fact]
        public async Task Outgoing_status_and_receipt_forward()
        {
            var host = new FakeHost();
            var node = new BinaryNode { Tag = "receipt" };

            await SocketFacadeWireDispatch.HandleOutgoingStatusAsync(
                host,
                "m1",
                "SERVER_ACK",
                null);
            await SocketFacadeWireDispatch.HandleReceiptAsync(host, node);

            Assert.Equal(
                new[] { "outgoing:m1|SERVER_ACK", "receipt:receipt" },
                host.Log);
        }

        [Fact]
        public void Link_code_raises_only_on_current_socket()
        {
            var host = new FakeHost();
            var node = new BinaryNode { Tag = "link_code_companion_reg" };

            SocketFacadeWireDispatch.HandleLinkCodeCompanion(
                host,
                node,
                isCurrentSocket: false);
            Assert.Empty(host.Log);

            SocketFacadeWireDispatch.HandleLinkCodeCompanion(
                host,
                node,
                isCurrentSocket: true);
            Assert.Equal(new[] { "link:link_code_companion_reg" }, host.Log);
        }

        private sealed class FakeHost : ISocketFacadeWireHost
        {
            public List<string> Log { get; } = new List<string>();

            public void NotifyPresenceUpdated(PresenceUpdateEventArgs args) =>
                Log.Add("presence:" + args.Jid);

            public void NoteMissingMessageFromWire(
                string chatJid,
                string participant,
                string messageId,
                bool isFromMe,
                DateTime timestamp,
                string reason) =>
                Log.Add("missing:" + chatJid + "|" + messageId + "|" + reason);

            public Task NotifyOutgoingStatusAsync(
                string messageId,
                string status,
                string error)
            {
                Log.Add("outgoing:" + messageId + "|" + status);
                return Task.CompletedTask;
            }

            public Task NotifyReceiptAsync(BinaryNode node)
            {
                Log.Add("receipt:" + node.Tag);
                return Task.CompletedTask;
            }

            public void RaiseLinkCodeCompanionReg(BinaryNode node) =>
                Log.Add("link:" + node.Tag);
        }
    }
}