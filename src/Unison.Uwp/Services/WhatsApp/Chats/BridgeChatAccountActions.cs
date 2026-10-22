// =============================================================================
// BridgeChatAccountActions
//
// Forwards IChatAccountActions to the live IWhatsAppSocket from the bridge.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Socket.AppState;
using Unison.Socket.UseCases.Messages;
using Unison.Uwp.Client;
using Unison.Uwp.Services.Socket;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.Uwp.Services.WhatsApp.Chats
{
    public sealed class BridgeChatAccountActions : IChatAccountActions
    {
        private readonly IWhatsAppSessionProvider _sessions;

        public BridgeChatAccountActions(IWhatsAppSessionProvider sessions)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        }

        public bool IsAvailable => _sessions.Socket != null;

        public Task SetPinnedAsync(string jid, bool pinned)
        {
            IWhatsAppSocket socket = _sessions.Socket;
            if (socket == null)
            {
                return Task.CompletedTask;
            }

            return socket.SetChatPinnedAsync(jid, pinned);
        }

        public Task DeleteChatAsync(string jid, IEnumerable<RangeMessage> lastMessages)
        {
            IWhatsAppSocket socket = _sessions.Socket;
            if (socket == null)
            {
                return Task.CompletedTask;
            }

            return socket.DeleteChatAsync(jid, lastMessages);
        }

        public Task MarkChatReadAsync(string jid, IEnumerable<RangeMessage> lastMessages)
        {
            IWhatsAppSocket socket = _sessions.Socket;
            if (socket == null)
            {
                return Task.CompletedTask;
            }

            return socket.MarkChatReadAsync(jid, lastMessages);
        }

        public Task MarkMessagesReadAsync(IEnumerable<ReceiptTarget> targets)
        {
            IWhatsAppSocket socket = _sessions.Socket;
            if (socket == null)
            {
                return Task.CompletedTask;
            }

            return socket.MarkMessagesReadAsync(targets);
        }
    }
}
