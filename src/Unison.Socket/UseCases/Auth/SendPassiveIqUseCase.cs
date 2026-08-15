// =============================================================================
// SendPassiveIqUseCase
//
// Tells the server whether this companion is the one the user is looking at.
//
// An active companion receives the live stream; a passive one stays connected
// without claiming the foreground. The server needs to be told explicitly after
// login - it does not assume - which is why a client that never sends this can
// sit connected and quiet, wondering where the traffic went.
//
// Ports: rc14 sendPassiveIq in src/Socket/socket.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Client;
using Unison.Baileys.Protocol;
using Unison.Socket.Session;

namespace Unison.Socket.UseCases.Auth
{
    public sealed class SendPassiveIqUseCase
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

        private readonly ConnectionHandler _connection;

        public SendPassiveIqUseCase(ConnectionHandler connection)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            _connection = connection;
        }

        /// <param name="active">
        /// True to take the foreground. False parks the connection, which is what a background
        /// or secondary session should do.
        /// </param>
        public Task<BinaryNode> ExecuteAsync(bool active, TimeSpan? timeout = null)
        {
            var iq = new BinaryNode(
                "iq",
                new Dictionary<string, string>
                {
                    { "to", WA.S_WHATSAPP_NET },
                    { "type", "set" },
                    { "xmlns", "passive" }
                },
                new List<BinaryNode> { new BinaryNode(active ? "active" : "passive") });

            return _connection.QueryAsync(iq, timeout ?? DefaultTimeout);
        }
    }
}
