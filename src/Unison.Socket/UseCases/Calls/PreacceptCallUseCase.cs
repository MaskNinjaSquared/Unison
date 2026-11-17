// =============================================================================
// PreacceptCallUseCase
//
// Eager prep on inbound offer (meowcaller sendPreaccept). Independent of Answer.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Messages;
using Unison.Socket.Session;

namespace Unison.Socket.UseCases.Calls
{
    public sealed class PreacceptCallUseCase
    {
        private readonly ConnectionHandler _connection;
        private readonly ISocketLog _log;

        public PreacceptCallUseCase(ConnectionHandler connection, ISocketLog log = null)
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

            BinaryNode node = CallStanzaBuilders.BuildPreaccept(
                callId,
                toJid,
                callCreatorJid,
                stanzaId,
                meJid);

            _log.Info(
                "[Call] preaccept send callId=" + callId +
                " to=" + toJid +
                " creator=" + callCreatorJid +
                " id=" + stanzaId);

            try
            {
                await _connection.SendNodeAsync(node).ConfigureAwait(false);
                _log.Info("[Call] preaccept sent callId=" + callId);
            }
            catch (Exception ex)
            {
                _log.Warn("[Call] Failed to send preaccept for " + callId, ex);
                throw;
            }
        }
    }
}
