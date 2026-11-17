// =============================================================================
// AcceptCallUseCase
//
// Answers an inbound ring on the wire (meowcaller BuildAccept, minimal body).
// Media/engine attach is still Phase 4 — this stops the caller ring.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Messages;
using Unison.Socket.Session;

namespace Unison.Socket.UseCases.Calls
{
    public sealed class AcceptCallUseCase
    {
        private readonly ConnectionHandler _connection;
        private readonly ISocketLog _log;

        public AcceptCallUseCase(ConnectionHandler connection, ISocketLog log = null)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _log = log ?? NullSocketLog.Instance;
        }

        public async Task ExecuteAsync(
            string callId,
            string toJid,
            string callCreatorJid,
            bool isVideo = false)
        {
            string meJid = _connection.MeId;
            string stanzaId = !string.IsNullOrEmpty(meJid)
                ? MessageContent.GenerateMessageId(meJid)
                : _connection.GenerateMessageTag();

            BinaryNode node = CallStanzaBuilders.BuildAccept(
                callId,
                toJid,
                callCreatorJid,
                stanzaId,
                isVideo,
                meJid);

            _log.Info(
                "[Call] accept send callId=" + callId +
                " to=" + toJid +
                " creator=" + callCreatorJid +
                " video=" + isVideo +
                " id=" + stanzaId);

            try
            {
                await _connection.SendNodeAsync(node).ConfigureAwait(false);
                _log.Info("[Call] accept sent callId=" + callId);
            }
            catch (Exception ex)
            {
                _log.Warn("[Call] Failed to send accept for " + callId, ex);
                throw;
            }
        }
    }
}