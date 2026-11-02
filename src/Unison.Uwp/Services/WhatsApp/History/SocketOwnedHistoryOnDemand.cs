// =============================================================================
// SocketOwnedHistoryOnDemand
//
// Thin IHistoryOnDemandSocket over the client's current socket field. Used by
// HistoryOnDemandDispatch inside WhatsAppService so the client no longer
// implements the port itself.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Uwp.Client;

namespace Unison.Uwp.Services.WhatsApp.History
{
    internal sealed class SocketOwnedHistoryOnDemand : IHistoryOnDemandSocket
    {
        private readonly Func<IWhatsAppSocket> _socket;

        public SocketOwnedHistoryOnDemand(Func<IWhatsAppSocket> socket)
        {
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        }

        public bool IsReady
        {
            get
            {
                IWhatsAppSocket socket = _socket();
                return socket != null && socket.IsHandshakeComplete;
            }
        }

        public string GenerateMessageId() => _socket().GenerateMessageId();

        public Task<string> RequestChatHistoryAsync(
            string chatJid,
            string oldestMessageId,
            bool oldestFromMe,
            long oldestTimestampMs,
            int count,
            string stanzaId) =>
            _socket().RequestHistorySyncOnDemandAsync(
                chatJid,
                oldestMessageId,
                oldestFromMe,
                oldestTimestampMs,
                count,
                stanzaId);

        public Task<string> RequestFullHistoryAsync(string stanzaId, string requestId = null) =>
            _socket().RequestFullHistorySyncOnDemandAsync(stanzaId, requestId);
    }
}
