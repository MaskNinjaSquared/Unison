// =============================================================================
// RejectCallUseCase
//
// Declines an inbound offer. Ports: Baileys rejectCall + meowcaller BuildReject.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Messages;
using Unison.Socket.Session;

namespace Unison.Socket.UseCases.Calls
{
    public sealed class RejectCallUseCase
    {
        private readonly ConnectionHandler _connection;
        private readonly ISocketLog _log;

        public RejectCallUseCase(ConnectionHandler connection, ISocketLog log = null)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _log = log ?? NullSocketLog.Instance;
        }

        public async Task ExecuteAsync(string callId, string toJid, string callCreatorJid)
        {
            string meJid = _connection.MeId;
            string stanzaId = !string.IsNullOrEmpty(meJid)
                ? MessageContent.GenerateMessageId(meJid)
                : _connection.GenerateMessageTag();

            BinaryNode node = CallStanzaBuilders.BuildReject(
                callId,
                toJid,
                callCreatorJid,
                meJid,
                stanzaId);

            _log.Info(
                "[Call] reject send callId=" + callId +
                " to=" + toJid +
                " creator=" + callCreatorJid +
                " me=" + (meJid ?? string.Empty) +
                " id=" + stanzaId);

            try
            {
                // Meowcaller SendNode; Baileys uses query — send is enough for the peer to stop
                // ringing; waiting for an ack can hang companions when the server stays silent.
                await _connection.SendNodeAsync(node).ConfigureAwait(false);
                _log.Info("[Call] reject sent callId=" + callId);
            }
            catch (Exception ex)
            {
                _log.Warn("[Call] Failed to send reject for " + callId, ex);
                throw;
            }
        }
    }
}
