// =============================================================================
// SendMessageAckUseCase
//
// Tells the server we took delivery of a stanza - or that we could not.
//
// Ports: rc14 sendMessageAck in src/Socket/messages-recv.ts
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Messages;
using Unison.Socket.Session;

namespace Unison.Socket.UseCases.Messages
{
    public sealed class SendMessageAckUseCase
    {
        private readonly ConnectionHandler _connection;
        private readonly Func<string> _meId;
        private readonly ISocketLog _log;

        /// <param name="meId">
        /// Resolved late: acks are sent from the moment stanzas arrive, which can be before the
        /// login node has told us who we are.
        /// </param>
        public SendMessageAckUseCase(ConnectionHandler connection, Func<string> meId, ISocketLog log = null)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            _connection = connection;
            _meId = meId ?? (() => null);
            _log = log ?? NullSocketLog.Instance;
        }

        /// <param name="errorCode">A <see cref="NackReason"/> value, or null for a plain ack.</param>
        public async Task ExecuteAsync(BinaryNode node, int? errorCode = null)
        {
            if (node == null)
            {
                return;
            }

            var ack = StanzaAck.Build(node, errorCode, _meId());

            try
            {
                await _connection.SendNodeAsync(ack).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A failed ack is not worth losing the stanza over: the server will resend it.
                _log.Warn("[Ack] Failed to ack a " + node.Tag + " node", ex);
            }
        }
    }
}
