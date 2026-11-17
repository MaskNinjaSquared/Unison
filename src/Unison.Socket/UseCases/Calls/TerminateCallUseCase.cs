// =============================================================================
// TerminateCallUseCase
//
// Ends an active / outgoing call. Ports: meowcaller BuildTerminate.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Messages;
using Unison.Socket.Session;

namespace Unison.Socket.UseCases.Calls
{
    public sealed class TerminateCallUseCase
    {
        private readonly ConnectionHandler _connection;
        private readonly ISocketLog _log;

        public TerminateCallUseCase(ConnectionHandler connection, ISocketLog log = null)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _log = log ?? NullSocketLog.Instance;
        }

        public async Task ExecuteAsync(
            string callId,
            string toJid,
            string callCreatorJid,
            string reason = null)
        {
            // meowcaller hangup: wrapper has to + id only (no from=me — that is reject/Baileys).
            string meJid = _connection.MeId;
            string stanzaId = !string.IsNullOrEmpty(meJid)
                ? MessageContent.GenerateMessageId(meJid)
                : _connection.GenerateMessageTag();

            BinaryNode node = CallStanzaBuilders.BuildTerminate(
                callId,
                toJid,
                callCreatorJid,
                reason,
                meJid: null,
                stanzaId: stanzaId);

            _log.Info(
                "[Call] terminate send callId=" + callId +
                " to=" + toJid +
                " creator=" + callCreatorJid +
                " reason=" + (reason ?? string.Empty) +
                " id=" + stanzaId);

            try
            {
                await _connection.SendNodeAsync(node).ConfigureAwait(false);
                _log.Info("[Call] terminate sent callId=" + callId);
            }
            catch (Exception ex)
            {
                _log.Warn("[Call] Failed to send terminate for " + callId, ex);
                throw;
            }
        }
    }
}
