// =============================================================================
// TearDownSocketForSoftReconnectOwner
//
// Owns ITearDownSocketForSoftReconnectHost. The socket field and its concrete
// type stay on the host; detach and dispose are ports.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class TearDownSocketForSoftReconnectOwner : ITearDownSocketForSoftReconnectHost
    {
        private readonly Func<object> _detachCurrentSocket;
        private readonly Action<object> _disconnectAndDispose;

        public TearDownSocketForSoftReconnectOwner(
            Func<object> detachCurrentSocket,
            Action<object> disconnectAndDispose)
        {
            _detachCurrentSocket = detachCurrentSocket
                ?? throw new ArgumentNullException(nameof(detachCurrentSocket));
            _disconnectAndDispose = disconnectAndDispose
                ?? throw new ArgumentNullException(nameof(disconnectAndDispose));
        }

        public void Run() => TearDownSocketForSoftReconnectDispatch.Run(this);

        object ITearDownSocketForSoftReconnectHost.DetachCurrentSocket() => _detachCurrentSocket();

        void ITearDownSocketForSoftReconnectHost.DisconnectAndDisposeSocket(object socket) =>
            _disconnectAndDispose(socket);
    }
}
