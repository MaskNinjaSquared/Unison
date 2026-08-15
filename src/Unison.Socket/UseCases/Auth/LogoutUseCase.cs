// =============================================================================
// LogoutUseCase
//
// Tells WhatsApp to forget this companion, then closes the connection.
//
// Wiping the local keys is not a logout. The phone keeps listing the device
// under "linked devices", the server keeps routing to a session nobody is
// reading, and the user has to unlink by hand from a screen they did not
// expect to need. The one stanza below is what turns a local wipe into an
// actual unlink, and it has to leave before the socket goes down - which is
// why ending the connection belongs here rather than to the caller.
//
// Ports: rc14 logout in src/Socket/socket.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Client;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Session;

namespace Unison.Socket.UseCases.Auth
{
    public sealed class LogoutUseCase
    {
        private readonly ConnectionHandler _connection;
        private readonly AuthState _auth;
        private readonly ISocketLog _log;

        public LogoutUseCase(ConnectionHandler connection, AuthState auth, ISocketLog log = null)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            if (auth == null)
            {
                throw new ArgumentNullException(nameof(auth));
            }

            _connection = connection;
            _auth = auth;
            _log = log ?? NullSocketLog.Instance;
        }

        /// <summary>
        /// Unlinks this device and ends the session with <see cref="DisconnectReason.LoggedOut"/>,
        /// so every listener sees the same outcome a revoke from the phone produces.
        /// </summary>
        /// <param name="reason">Text carried on the close, for logs. Not sent to the server.</param>
        /// <remarks>
        /// Best effort by design. If the stanza cannot be sent - no identity yet, socket already
        /// gone, server refusing - the connection is still closed, because a failure to notify is
        /// not a reason to leave the user connected to a session they asked to leave.
        /// </remarks>
        public async Task ExecuteAsync(string reason = null)
        {
            var me = _auth.Me == null ? null : _auth.Me.Id;

            if (!string.IsNullOrEmpty(me) && _connection.IsConnected)
            {
                try
                {
                    await _connection.SendNodeAsync(BuildRemoveCompanionNode(me)).ConfigureAwait(false);
                    _log.Info("Asked the server to unlink this device");
                }
                catch (Exception ex)
                {
                    _log.Warn("Could not tell the server we are unlinking; closing anyway", ex);
                }
            }

            await _connection
                .EndAsync(new WaConnectionException(reason ?? "Intentional Logout", DisconnectReason.LoggedOut))
                .ConfigureAwait(false);
        }

        private BinaryNode BuildRemoveCompanionNode(string me)
        {
            return new BinaryNode(
                "iq",
                new Dictionary<string, string>
                {
                    { "to", WA.S_WHATSAPP_NET },
                    { "type", "set" },
                    { "id", _connection.GenerateMessageTag() },
                    { "xmlns", "md" }
                },
                new List<BinaryNode>
                {
                    new BinaryNode(
                        "remove-companion-device",
                        new Dictionary<string, string>
                        {
                            { "jid", me },
                            { "reason", "user_initiated" }
                        })
                });
        }
    }
}
