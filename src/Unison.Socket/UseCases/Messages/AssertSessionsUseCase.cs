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

            for (int i = 0; i < unique.Count; i++)
            {
                string jid = unique[i];
                if (!force)
                {
                    var validation = await _signal.ValidateSessionAsync(jid).ConfigureAwait(false);
                    if (validation != null && validation.Exists)
                    {
                        continue;
                    }
                }

                missing.Add(jid);

                if ((i + 1) % 100 == 0)
                {
                    _log.Info(
                        "[Send] Group assert-sessions scanning " + (i + 1) + "/" + unique.Count +
                        "; missingSoFar=" + missing.Count);
                    await Task.Yield();
                }
            }

            _log.Info(
                "[Send] Group assert-sessions validated; have=" + (unique.Count - missing.Count) +
                "; missing=" + missing.Count + "; total=" + unique.Count);

            if (missing.Count == 0)
            {
                return false;
            }

            // One IQ with hundreds of users (status@broadcast SKDM) stalls or times out.
            // Fetch in chunks so a Status publish to "My contacts" can finish.
            // Inject also used to await StorageFile per bundle — defer disk for the batch.
            bool deferPersist = missing.Count >= DeferredSessionPersistThreshold;
            if (deferPersist)
            {
                _signal.BeginDeferredSessionPersist();
            }

            int injectedTotal = 0;
            try
            {
                for (int offset = 0; offset < missing.Count; offset += PrekeyFetchChunkSize)
                {
                    int count = Math.Min(PrekeyFetchChunkSize, missing.Count - offset);
                    var chunk = missing.GetRange(offset, count);
                    var userNodes = chunk
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

                    _log.Info(
                        "[Send] Group assert-sessions fetch " + (offset + chunk.Count) + "/" +
                        missing.Count + " (chunk=" + chunk.Count + ")");

                    var response = await _connection.QueryAsync(iq).ConfigureAwait(false);
                    int injected = await E2ESessionParser
                        .ParseAndInjectAsync(response, _signal)
                        .ConfigureAwait(false);
                    injectedTotal += injected;

                    _log.Info(
                        "[Send] Group assert-sessions inject " + (offset + chunk.Count) + "/" +
                        missing.Count + "; opened=" + injected);
                }
            }
            finally
            {
                if (deferPersist)
                {
                    await _signal.EndDeferredSessionPersistAsync().ConfigureAwait(false);
                }
            }

            _log.Info("[Send] Group assert-sessions opened " + injectedTotal + " session(s)");
            return injectedTotal > 0;
        }

        /// <summary>Max user nodes per encrypt IQ — keeps status SKDM prekey fetches responsive.</summary>
        private const int PrekeyFetchChunkSize = 100;

        private const int DeferredSessionPersistThreshold = 16;

    }
}
