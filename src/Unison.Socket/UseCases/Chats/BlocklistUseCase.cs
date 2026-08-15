// =============================================================================
// BlocklistUseCase
//
// Reads who is blocked, and blocks or unblocks someone.
//
// The list is not part of app state and is not pushed on login: it has to be
// asked for. Changes made on the phone arrive later as blocklist notifications,
// so the fetch is a starting point rather than a poll.
//
// Ports: rc14 fetchBlocklist and updateBlockStatus in src/Socket/chats.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Session;

namespace Unison.Socket.UseCases.Chats
{
    public sealed class BlocklistUseCase
    {
        private readonly ConnectionHandler _connection;

        public BlocklistUseCase(ConnectionHandler connection)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            _connection = connection;
        }

        public async Task<List<string>> FetchAsync(TimeSpan? timeout = null)
        {
            var iq = new BinaryNode(
                "iq",
                new Dictionary<string, string>
                {
                    { "to", WA.S_WHATSAPP_NET },
                    { "type", "get" },
                    { "xmlns", "blocklist" }
                });

            var response = await _connection.QueryAsync(iq, timeout).ConfigureAwait(false);

            var blocked = new List<string>();
            var list = response != null ? response.GetChild("list") : null;
            if (list == null)
            {
                return blocked;
            }

            var items = list.GetChildren("item");
            if (items != null)
            {
                foreach (var item in items)
                {
                    var jid = item.GetAttribute("jid");
                    if (!string.IsNullOrEmpty(jid))
                    {
                        blocked.Add(jid);
                    }
                }
            }

            return blocked;
        }

        public Task UpdateAsync(string jid, bool block, TimeSpan? timeout = null)
        {
            if (string.IsNullOrEmpty(jid))
            {
                throw new ArgumentException("jid is required", nameof(jid));
            }

            var iq = new BinaryNode(
                "iq",
                new Dictionary<string, string>
                {
                    { "to", WA.S_WHATSAPP_NET },
                    { "type", "set" },
                    { "xmlns", "blocklist" }
                },
                new List<BinaryNode>
                {
                    new BinaryNode(
                        "item",
                        new Dictionary<string, string>
                        {
                            { "action", block ? "block" : "unblock" },
                            { "jid", jid }
                        })
                });

            return _connection.QueryAsync(iq, timeout);
        }
    }
}
