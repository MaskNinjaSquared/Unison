// =============================================================================
// BridgeMessageWireActions
//
// Forwards IMessageWireActions to the live IWhatsAppSocket from the bridge.
// =============================================================================
using System;
using System.Threading.Tasks;
using Proto;
using Unison.Uwp.Client;
using Unison.WhatsAppClient.Abstractions;
using UwpSessionProvider = Unison.Uwp.Services.Socket.IWhatsAppSessionProvider;

namespace Unison.Uwp.Services.WhatsApp.Messages
{
    /// <summary>
    /// Internal: constructor takes the UWP bridge provider (<c>internal</c>).
    /// </summary>
    internal sealed class BridgeMessageWireActions : IMessageWireActions
    {
        private readonly UwpSessionProvider _sessions;

        public BridgeMessageWireActions(UwpSessionProvider sessions)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        }

        public bool CanSubscribePresence
        {
            get
            {
                IWhatsAppSocket socket = _sessions.Socket;
                return socket != null && socket.IsConnected;
            }
        }

        public bool CanSendPin => _sessions.Socket != null;

        public bool CanSendDeleteForMe => _sessions.Socket != null;

        public bool CanSendDeleteForEveryone => _sessions.Socket != null;

        public Task PresenceSubscribeAsync(string jid)
        {
            IWhatsAppSocket socket = _sessions.Socket;
            if (socket == null || !socket.IsConnected)
            {
                return Task.CompletedTask;
            }

            return socket.PresenceSubscribeAsync(jid);
        }

        public Task SendPinInChatMessageAsync(
            string chatJid,
            MessageKey targetKey,
            bool pin,
            uint durationSeconds)
        {
            IWhatsAppSocket socket = _sessions.Socket;
            if (socket == null)
            {
                throw new InvalidOperationException("Not connected: the message pin was not sent.");
            }

            return socket.SendPinInChatMessageAsync(chatJid, targetKey, pin, durationSeconds);
        }

        public Task DeleteMessageForMeAsync(
            string chatJid,
            string messageId,
            bool fromMe,
            long messageTimestampUnixSeconds,
            bool deleteMedia)
        {
            IWhatsAppSocket socket = _sessions.Socket;
            if (socket == null)
            {
                return Task.CompletedTask;
            }

            return socket.DeleteMessageForMeAsync(
                chatJid,
                messageId,
                fromMe,
                messageTimestampUnixSeconds,
                deleteMedia);
        }

        public Task SendDeleteForEveryoneAsync(string chatJid, MessageKey targetKey)
        {
            IWhatsAppSocket socket = _sessions.Socket;
            if (socket == null)
            {
                throw new InvalidOperationException("Not connected: the message revoke was not sent.");
            }

            return socket.SendDeleteMessageAsync(chatJid, targetKey);
        }
    }
}
