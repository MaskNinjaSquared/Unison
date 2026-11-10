// =============================================================================
// BridgeStatusWireActions
//
// Forwards IStatusWireActions to the live IWhatsAppSocket from the bridge.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Uwp.Client;
using Unison.WhatsAppClient.Abstractions;
using UwpSessionProvider = Unison.Uwp.Services.Socket.IWhatsAppSessionProvider;

namespace Unison.Uwp.Services.WhatsApp.Messages
{
    internal sealed class BridgeStatusWireActions : IStatusWireActions
    {
        private readonly UwpSessionProvider _sessions;

        public BridgeStatusWireActions(UwpSessionProvider sessions)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        }

        public bool CanPublish
        {
            get
            {
                IWhatsAppSocket socket = _sessions.Socket;
                return socket != null && socket.IsConnected;
            }
        }

        public Task<string> SendTextStatusAsync(
            string text,
            uint backgroundArgb,
            uint textArgb,
            int font,
            IList<string> statusJidList)
        {
            IWhatsAppSocket socket = _sessions.Socket;
            if (socket == null || !socket.IsConnected)
            {
                throw new InvalidOperationException("Not connected: the Status was not published.");
            }

            return socket.SendTextStatusAsync(text, backgroundArgb, textArgb, font, statusJidList);
        }
    }
}
