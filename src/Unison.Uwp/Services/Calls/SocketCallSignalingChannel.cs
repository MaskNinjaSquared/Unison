using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Socket.UseCases.Calls;
using Unison.Voip.Abstractions;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.Uwp.Services.Calls
{
    /// <summary>
    /// Forwards reject/terminate onto the live session connection (meowcaller-shaped stanzas).
    /// </summary>
    public sealed class SocketCallSignalingChannel : ICallSignalingChannel
    {
        private readonly IWhatsAppSessionProvider _sessions;

        public SocketCallSignalingChannel(IWhatsAppSessionProvider sessions)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        }

        public Task RejectAsync(
            string callId,
            string toJid,
            string callCreatorJid,
            CancellationToken cancellationToken)
        {
            var connection = RequireConnection();
            return new RejectCallUseCase(connection).ExecuteAsync(callId, toJid, callCreatorJid);
        }

        public Task TerminateAsync(
            string callId,
            string toJid,
            string callCreatorJid,
            string reason,
            CancellationToken cancellationToken)
        {
            var connection = RequireConnection();
            return new TerminateCallUseCase(connection).ExecuteAsync(
                callId,
                toJid,
                callCreatorJid,
                reason);
        }

        private Unison.Socket.Session.ConnectionHandler RequireConnection()
        {
            var session = _sessions.Current;
            if (session == null || session.Connection == null)
            {
                throw new InvalidOperationException("No live WhatsApp session for call signaling.");
            }

            return session.Connection;
        }
    }
}
