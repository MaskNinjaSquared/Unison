using System;
using System.Threading.Tasks;
using Unison.Uwp.Client;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;

namespace Unison.Uwp.Services.WhatsApp
{
    /// <summary>
    /// <see cref="IConnectionSocketControl"/> over the shared socket slot.
    /// </summary>
    internal sealed class SlotConnectionSocketControl : IConnectionSocketControl
    {
        private readonly ConnectionSlot<IWhatsAppSocket> _socketSlot;

        public SlotConnectionSocketControl(ConnectionSlot<IWhatsAppSocket> socketSlot)
        {
            _socketSlot = socketSlot ?? throw new ArgumentNullException(nameof(socketSlot));
        }

        public object Current => _socketSlot.Current;

        public bool IsConnected
        {
            get
            {
                IWhatsAppSocket socket = _socketSlot.Current;
                return socket != null && socket.IsConnected && socket.IsHandshakeComplete;
            }
        }

        public bool Holds(object socket) => _socketSlot.Holds(socket as IWhatsAppSocket);

        public object AuthOf(object socket) => (socket as IWhatsAppSocket)?.Auth;

        public Task LogoutAsync(string reason) => _socketSlot.Current.LogoutAsync(reason);

        public object DetachCurrent()
        {
            IWhatsAppSocket staleSocket = _socketSlot.Current;
            _socketSlot.Current = null;
            return staleSocket;
        }

        public void DisconnectAndDispose(object socket)
        {
            var typed = socket as IWhatsAppSocket;
            if (typed == null)
            {
                return;
            }

            try { typed.Disconnect(); } catch { }
            try { typed.Dispose(); } catch { }
        }
    }
}
