// =============================================================================
// RejectCallUseCase
//
// Declines an inbound offer. Ports: meowcaller BuildReject + Baileys rejectCall.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Socket.Abstractions;
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
            var node = CallStanzaBuilders.BuildReject(callId, toJid, callCreatorJid);
            try
            {
                await _connection.SendNodeAsync(node).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn("[Call] Failed to send reject for " + callId, ex);
                throw;
            }
        }
    }
}
