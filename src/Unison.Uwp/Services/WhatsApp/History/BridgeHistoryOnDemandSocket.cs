// =============================================================================
// BridgeHistoryOnDemandSocket
//
// Forwards IHistoryOnDemandSocket to the live IWhatsAppSocket from the bridge.
// HistoryFacade reaches the wire through this port — not WhatsAppService.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Uwp.Client;
using UwpSessionProvider = Unison.Uwp.Services.Socket.IWhatsAppSessionProvider;

namespace Unison.Uwp.Services.WhatsApp.History
{
    internal sealed class BridgeHistoryOnDemandSocket : IHistoryOnDemandSocket
    {
        private readonly UwpSessionProvider _sessions;

        public BridgeHistoryOnDemandSocket(UwpSessionProvider sessions)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        }

        public bool IsReady
        {
            get
            {
                IWhatsAppSocket socket = _sessions.Socket;
                return socket != null && socket.IsHandshakeComplete;
            }
        }

        public string GenerateMessageId()
        {
            IWhatsAppSocket socket = _sessions.Socket;
            if (socket == null)
            {
                throw new InvalidOperationException("Not connected: no message id for on-demand history.");
            }

            return socket.GenerateMessageId();
        }

        public Task<string> RequestChatHistoryAsync(
            string chatJid,
            string oldestMessageId,
            bool oldestFromMe,
            long oldestTimestampMs,
            int count,
            string stanzaId)
        {
            IWhatsAppSocket socket = _sessions.Socket;
            if (socket == null)
            {
                throw new InvalidOperationException("Not connected: chat history on-demand was not sent.");
            }

            return socket.RequestHistorySyncOnDemandAsync(
                chatJid,
                oldestMessageId,
                oldestFromMe,
                oldestTimestampMs,
                count,
                stanzaId);
        }

        public Task<string> RequestFullHistoryAsync(string stanzaId, string requestId = null)
        {
            IWhatsAppSocket socket = _sessions.Socket;
            if (socket == null)
            {
                throw new InvalidOperationException("Not connected: full history on-demand was not sent.");
            }

            return socket.RequestFullHistorySyncOnDemandAsync(stanzaId, requestId);
        }
    }
}
