// =============================================================================
// CleanDirtyBitsUseCase
//
// Tells the server we have caught up with something it flagged as stale.
//
// The server does not push group changes to a companion that was offline. It
// raises a flag - <ib><dirty type="groups"/></ib> - and waits. Reading the
// flagged data is only half the exchange: until the bit is cleared the server
// keeps raising it on every connect, so a client that refreshes but never
// answers refreshes forever, which is how a login ends up spending its first
// seconds re-querying data it already has.
//
// Ports: rc14 cleanDirtyBits in src/Socket/chats.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Session;
using Unison.Socket.WABinary;

namespace Unison.Socket.UseCases.Chats
{
    public sealed class CleanDirtyBitsUseCase
    {
        private readonly ConnectionHandler _connection;

        public CleanDirtyBitsUseCase(ConnectionHandler connection)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            _connection = connection;
        }

        /// <param name="type">The flag being cleared: "groups" or "account_sync".</param>
        /// <param name="fromTimestamp">
        /// How far back we caught up to. Account syncs carry one, so the server clears only what
        /// we actually read; group flags do not.
        /// </param>
        public Task ExecuteAsync(string type, long? fromTimestamp = null)
        {
            if (string.IsNullOrEmpty(type))
            {
                throw new ArgumentException("A dirty bit type is required", nameof(type));
            }

            var attrs = new Dictionary<string, string> { { "type", type } };
            if (fromTimestamp.HasValue)
            {
                attrs["timestamp"] = fromTimestamp.Value.ToString();
            }

            // Sent, not queried: the server acknowledges by stopping, not by replying, and
            // waiting for a reply that never comes would hold a slot open for the full timeout.
            var iq = new BinaryNode(
                "iq",
                new Dictionary<string, string>
                {
                    { "to", JidUtils.ServerWhatsApp },
                    { "type", "set" },
                    { "xmlns", "urn:xmpp:whatsapp:dirty" },
                    { "id", _connection.GenerateMessageTag() }
                },
                new List<BinaryNode> { new BinaryNode("clean", attrs) });

            return _connection.SendNodeAsync(iq);
        }
    }
}
