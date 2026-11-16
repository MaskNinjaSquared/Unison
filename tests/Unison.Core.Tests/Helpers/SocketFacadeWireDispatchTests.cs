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
            var facts = new ReceiptFacts { Status = "read", MessageIds = new[] { "m1" } };

            await SocketFacadeWireDispatch.HandleOutgoingStatusAsync(
                host,
                "m1",
                "SERVER_ACK",
                null);
            await SocketFacadeWireDispatch.HandleReceiptAsync(host, facts);

            Assert.Equal(
                new[] { "outgoing:m1|SERVER_ACK", "receipt:read" },
                host.Log);
        }

        [Fact]
        public async Task Receipt_without_facts_is_dropped()
        {
            var host = new FakeHost();

            await SocketFacadeWireDispatch.HandleReceiptAsync(host, null);

            Assert.Empty(host.Log);
        }

        [Fact]
        public async Task Owner_reads_the_receipt_node_before_the_host_sees_it()
        {
            ReceiptFacts seen = null;
            var owner = NewOwner(facts => { seen = facts; return Task.CompletedTask; });
            var node = new BinaryNode(
                "receipt",
                new Dictionary<string, string>
                {
                    ["type"] = "read",
                    ["id"] = "m1",
                    ["from"] = "5511@s.whatsapp.net"
                });

            await owner.HandleReceiptAsync(node);

            Assert.NotNull(seen);
            Assert.Contains("m1", seen.MessageIds);
            Assert.False(seen.IsGroup);
        }

        [Fact]
        public async Task Owner_drops_retry_receipts()
        {
            bool called = false;
            var owner = NewOwner(_ => { called = true; return Task.CompletedTask; });
            var node = new BinaryNode(
                "receipt",
                new Dictionary<string, string> { ["type"] = "retry", ["id"] = "m1" });

            await owner.HandleReceiptAsync(node);

            Assert.False(called);
        }

        private static SocketFacadeWireOwner NewOwner(Func<ReceiptFacts, Task> notifyReceipt) =>
            new SocketFacadeWireOwner(
                new IdentityJids(),
                _ => { },
                (a, b, c, d, e, f) => { },
                (a, b, c) => Task.CompletedTask,
                notifyReceipt);

        private sealed class IdentityJids : Unison.Core.Contracts.IJidResolver
        {
            public string GetCanonicalJid(string jid) => jid;

            public string PeekCanonicalJid(string jid) => jid;

            public bool TryGetAlias(string jid, out string alias)
            {
                alias = null;
                return false;
            }

            public bool IsSelfLinked(string jid) => false;

            public Profile Self => null;
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

            public Task NotifyReceiptAsync(ReceiptFacts receipt)
            {
                Log.Add("receipt:" + receipt.Status);
                return Task.CompletedTask;
            }
        }
    }
}