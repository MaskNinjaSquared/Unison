// =============================================================================
// BridgeContactBlockActions
//
// Forwards block IQ to SocketBridge.UpdateBlockStatusAsync.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Uwp.Client;
using Unison.WhatsAppClient.Abstractions;
using UwpSessionProvider = Unison.Uwp.Services.Socket.IWhatsAppSessionProvider;

namespace Unison.Uwp.Services.WhatsApp.Contacts
{
    internal sealed class BridgeContactBlockActions : IContactBlockActions
    {
        private readonly UwpSessionProvider _sessions;

        public BridgeContactBlockActions(UwpSessionProvider sessions)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        }

        public bool CanBlock
        {
            get
            {
                IWhatsAppSocket socket = _sessions.Socket;
                return socket != null && socket.IsHandshakeComplete;
            }
        }

        public Task BlockAsync(string jid)
        {
            IWhatsAppSocket socket = _sessions.Socket;
            if (socket == null || !socket.IsHandshakeComplete)
            {
                return Task.FromException(
                    new InvalidOperationException("Not connected: block was not sent."));
            }

            return socket.UpdateBlockStatusAsync(jid, block: true);
        }
    }
}
