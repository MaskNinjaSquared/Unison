// =============================================================================
// RelayLatencyCallUseCase
//
// Answers one caller relaylatency probe as the callee (relay election).
// Ports: meowcaller engine.onRelayLatency + BuildRelayLatency.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Messages;
using Unison.Socket.Session;

namespace Unison.Socket.UseCases.Calls
{
    public sealed class RelayLatencyCallUseCase
    {
        private readonly ConnectionHandler _connection;
        private readonly ISocketLog _log;

        public RelayLatencyCallUseCase(ConnectionHandler connection, ISocketLog log = null)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _log = log ?? NullSocketLog.Instance;
        }

        public async Task ExecuteAsync(
            string callId,
            string toJid,
            string callCreatorJid,
            uint latencyMs,
            string relayName,
            byte[] addressBytes)
        {
            string meJid = _connection.MeId;
            string stanzaId = !string.IsNullOrEmpty(meJid)
                ? MessageContent.GenerateMessageId(meJid)
                : _connection.GenerateMessageTag();

            BinaryNode node = CallStanzaBuilders.BuildRelayLatency(
                callId,
                toJid,
                callCreatorJid,
                latencyMs,
                relayName,
                addressBytes,
                stanzaId);

            try
            {
                await _connection.SendNodeAsync(node).ConfigureAwait(false);
                _log.Info(
                    "[Call] relaylatency sent callId=" + callId +
                    " relay=" + relayName +
                    " latencyMs=" + latencyMs);
            }
            catch (Exception ex)
            {
                _log.Warn("[Call] Failed to send relaylatency for " + callId, ex);
                throw;
            }
        }
    }
}
