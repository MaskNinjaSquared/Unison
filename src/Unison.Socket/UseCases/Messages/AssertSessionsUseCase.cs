// =============================================================================
// AssertSessionsUseCase
//
// Makes sure there is a usable Signal session with everyone we are about to
// encrypt for.
//
// One query fetches bundles for all the missing peers at once, under exactly the
// JIDs it was given. That last part is the whole contract: a session is stored
// under the address it was opened for, and the send path then encrypts under
// that same address. Translating a phone number to its LID here - which this
// used to do - opens the session under one identity while the caller encrypts
// under the other, and every message fails with no session found. Deciding which
// address space a conversation lives in belongs to the caller, above this.
//
// Ports: rc14 assertSessions in src/Socket/messages-send.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Session;
using Unison.Socket.Signal;
using Unison.Socket.WABinary;

namespace Unison.Socket.UseCases.Messages
{
    public sealed class AssertSessionsUseCase
    {
        private readonly ConnectionHandler _connection;
        private readonly ISignalRepository _signal;
        private readonly ISocketLog _log;

        public AssertSessionsUseCase(ConnectionHandler connection, ISignalRepository signal, ISocketLog log = null)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            if (signal == null)
            {
                throw new ArgumentNullException(nameof(signal));
            }

            _connection = connection;
            _signal = signal;
            _log = log ?? NullSocketLog.Instance;
        }

        /// <param name="force">
        /// Fetch bundles even for peers we already have a session with, and tell the server the
        /// reason is an identity change. Used after a LID mapping is learned.
        /// </param>
        /// <returns>True when at least one new session was opened.</returns>
        public async Task<bool> ExecuteAsync(IEnumerable<string> jids, bool force = false)
        {
            if (jids == null)
            {
                return false;
            }

            var unique = jids.Where(jid => !string.IsNullOrEmpty(jid)).Distinct(StringComparer.Ordinal).ToList();
            var missing = new List<string>();

            foreach (var jid in unique)
            {
                if (!force)
                {
                    var validation = await _signal.ValidateSessionAsync(jid).ConfigureAwait(false);
                    if (validation != null && validation.Exists)
                    {
                        continue;
                    }
                }

                missing.Add(jid);
            }

            if (missing.Count == 0)
            {
                return false;
            }

            var userNodes = missing
                .Select(jid =>
                {
                    var attrs = new Dictionary<string, string> { { "jid", jid } };
                    if (force)
                    {
                        attrs["reason"] = "identity";
                    }

                    return new BinaryNode("user", attrs);
                })
                .ToList();

            var iq = new BinaryNode(
                "iq",
                new Dictionary<string, string>
                {
                    { "xmlns", "encrypt" },
                    { "type", "get" },
                    { "to", WA.S_WHATSAPP_NET }
                },
                new List<BinaryNode> { new BinaryNode("key", null, userNodes) });

            _log.Debug("[Sessions] Fetching " + missing.Count + " prekey bundle(s)");

            var response = await _connection.QueryAsync(iq).ConfigureAwait(false);
            var injected = await E2ESessionParser.ParseAndInjectAsync(response, _signal).ConfigureAwait(false);

            _log.Debug("[Sessions] Opened " + injected + " session(s)");
            return injected > 0;
        }

    }
}
