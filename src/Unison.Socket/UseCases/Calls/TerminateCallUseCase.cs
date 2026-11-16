// =============================================================================
// TerminateCallUseCase
//
// Ends an active / outgoing call. Ports: meowcaller BuildTerminate.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Socket.Abstractions;
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
            var node = CallStanzaBuilders.BuildTerminate(callId, toJid, callCreatorJid, reason);
            try
            {
                await _connection.SendNodeAsync(node).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn("[Call] Failed to send terminate for " + callId, ex);
                throw;
            }
        }
    }
}
