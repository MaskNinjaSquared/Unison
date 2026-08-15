// =============================================================================
// ExecuteUSyncQueryUseCase
//
// Sends a usync query and hands back the parsed reply.
//
// Every contact lookup funnels through here, so the envelope - the iq wrapper,
// the sid, the fixed last/index attributes - is written once. What varies is the
// query object the caller composed, which keeps the per-feature UseCases down to
// "build the columns, read the answer".
//
// Ports: rc14 executeUSyncQuery in src/Socket/socket.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Session;
using Unison.Socket.USync;

namespace Unison.Socket.UseCases.USync
{
    public sealed class ExecuteUSyncQueryUseCase
    {
        private readonly ConnectionHandler _connection;

        public ExecuteUSyncQueryUseCase(ConnectionHandler connection)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            _connection = connection;
        }

        public async Task<USyncQueryResult> ExecuteAsync(USyncQuery query, TimeSpan? timeout = null)
        {
            if (query == null)
            {
                throw new ArgumentNullException(nameof(query));
            }

            if (query.Protocols.Count == 0)
            {
                throw new InvalidOperationException("A usync query needs at least one protocol");
            }

            var userNodes = new List<BinaryNode>();
            foreach (var user in query.Users)
            {
                var children = new List<BinaryNode>();
                foreach (var protocol in query.Protocols)
                {
                    var element = protocol.GetUserElement(user);
                    if (element != null)
                    {
                        children.Add(element);
                    }
                }

                // A row identified by phone number carries no jid attribute: the number itself
                // is the question, and it travels inside the contact column.
                var attrs = new Dictionary<string, string>();
                if (string.IsNullOrEmpty(user.Phone) && !string.IsNullOrEmpty(user.Id))
                {
                    attrs["jid"] = user.Id;
                }

                userNodes.Add(new BinaryNode("user", attrs, children.Count > 0 ? children : null));
            }

            var queryChildren = new List<BinaryNode>();
            foreach (var protocol in query.Protocols)
            {
                var element = protocol.GetQueryElement();
                if (element != null)
                {
                    queryChildren.Add(element);
                }
            }

            var usync = new BinaryNode(
                "usync",
                new Dictionary<string, string>
                {
                    { "context", query.Context },
                    { "mode", query.Mode },
                    { "sid", _connection.GenerateMessageTag() },
                    { "last", "true" },
                    { "index", "0" }
                },
                new List<BinaryNode>
                {
                    new BinaryNode("query", null, queryChildren),
                    new BinaryNode("list", null, userNodes)
                });

            var iq = new BinaryNode(
                "iq",
                new Dictionary<string, string>
                {
                    { "to", WA.S_WHATSAPP_NET },
                    { "type", "get" },
                    { "xmlns", "usync" }
                },
                new List<BinaryNode> { usync });

            var response = await _connection.QueryAsync(iq, timeout).ConfigureAwait(false);
            return query.ParseResult(response);
        }
    }
}
