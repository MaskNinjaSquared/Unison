using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Socket.Abstractions;
using Unison.Socket.Messages;
using Unison.Socket.Session;
using Unison.Socket.UseCases.Calls;
using Unison.Uwp.Client;
using Unison.Voip.Abstractions;
using AppSessionProvider = Unison.WhatsAppClient.Abstractions.IWhatsAppSessionProvider;

namespace Unison.Uwp.Services.Calls
{
    /// <summary>
    /// Forwards offer/preaccept/accept/reject/terminate onto the live session connection.
    /// </summary>
    public sealed class SocketCallSignalingChannel : ICallSignalingChannel
    {
        private readonly AppSessionProvider _sessions;
        private readonly Func<MessageModule> _messages;

        public SocketCallSignalingChannel(
            AppSessionProvider sessions,
            Func<MessageModule> messages = null)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _messages = messages;
        }

        public async Task<CallOfferWireResult> OfferAudioAsync(
            string peerJid,
            CancellationToken cancellationToken)
        {
            MessageModule module = RequireMessages();
            OfferCallResult result = await module.OfferCall
                .ExecuteAsync(peerJid)
                .ConfigureAwait(false);
            return new CallOfferWireResult
            {
                CallId = result.CallId,
                PeerJid = result.PeerJid,
                CallCreatorJid = result.CallCreatorJid,
                CallKey = result.CallKey
            };
        }

        public Task PreacceptAsync(
            string callId,
            string toJid,
            string callCreatorJid,
            CancellationToken cancellationToken)
        {
            ConnectionHandler connection;
            ISocketLog log;
            Require(out connection, out log);
            return new PreacceptCallUseCase(connection, log).ExecuteAsync(callId, toJid, callCreatorJid);
        }

        public Task AcceptAsync(
            string callId,
            string toJid,
            string callCreatorJid,
            bool isVideo,
            CancellationToken cancellationToken)
        {
            ConnectionHandler connection;
            ISocketLog log;
            Require(out connection, out log);
            return new AcceptCallUseCase(connection, log).ExecuteAsync(
                callId,
                toJid,
                callCreatorJid,
                isVideo);
        }

        public Task RejectAsync(
            string callId,
            string toJid,
            string callCreatorJid,
            CancellationToken cancellationToken)
        {
            ConnectionHandler connection;
            ISocketLog log;
            Require(out connection, out log);
            return new RejectCallUseCase(connection, log).ExecuteAsync(callId, toJid, callCreatorJid);
        }

        public Task TerminateAsync(
            string callId,
            string toJid,
            string callCreatorJid,
            string reason,
            CancellationToken cancellationToken)
        {
            ConnectionHandler connection;
            ISocketLog log;
            Require(out connection, out log);
            return new TerminateCallUseCase(connection, log).ExecuteAsync(
                callId,
                toJid,
                callCreatorJid,
                reason);
        }

        public Task SendRelayLatencyAsync(
            string callId,
            string toJid,
            string callCreatorJid,
            uint latencyMs,
            string relayName,
            byte[] addressBytes,
            CancellationToken cancellationToken)
        {
            ConnectionHandler connection;
            ISocketLog log;
            Require(out connection, out log);
            return new RelayLatencyCallUseCase(connection, log).ExecuteAsync(
                callId,
                toJid,
                callCreatorJid,
                latencyMs,
                relayName,
                addressBytes);
        }

        private void Require(out ConnectionHandler connection, out ISocketLog log)
        {
            WhatsAppSession session = _sessions.Current;
            if (session == null || session.Connection == null)
            {
                throw new InvalidOperationException("No live WhatsApp session for call signaling.");
            }

            connection = session.Connection;
            log = session.Log ?? NullSocketLog.Instance;
        }

        private MessageModule RequireMessages()
        {
            MessageModule module = _messages != null ? _messages() : null;
            if (module == null || module.OfferCall == null)
            {
                throw new InvalidOperationException("No live message module for call offer.");
            }

            return module;
        }
    }
}
