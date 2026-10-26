// =============================================================================
// InvalidateCurrentSocketOwner
//
// Owns IInvalidateCurrentSocketHost. Health-monitor stop / field clear /
// disconnect+dispose stay as ctor ports.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class InvalidateCurrentSocketOwner : IInvalidateCurrentSocketHost
    {
        private readonly Func<object, bool> _isCurrentSocket;
        private readonly Action<string> _stopHealthMonitor;
        private readonly Action _clearCurrentSocketField;
        private readonly Action<object> _disconnectAndDispose;
        private readonly Action<string> _writeJournal;

        public InvalidateCurrentSocketOwner(
            Func<object, bool> isCurrentSocket,
            Action<string> stopHealthMonitor,
            Action clearCurrentSocketField,
            Action<object> disconnectAndDispose,
            Action<string> writeJournal)
        {
            _isCurrentSocket = isCurrentSocket
                ?? throw new ArgumentNullException(nameof(isCurrentSocket));
            _stopHealthMonitor = stopHealthMonitor
                ?? throw new ArgumentNullException(nameof(stopHealthMonitor));
            _clearCurrentSocketField = clearCurrentSocketField
                ?? throw new ArgumentNullException(nameof(clearCurrentSocketField));
            _disconnectAndDispose = disconnectAndDispose
                ?? throw new ArgumentNullException(nameof(disconnectAndDispose));
            _writeJournal = writeJournal
                ?? throw new ArgumentNullException(nameof(writeJournal));
        }

        public void Run(object socket, string reason) =>
            InvalidateCurrentSocketDispatch.Run(this, socket, reason);

        bool IInvalidateCurrentSocketHost.IsCurrentSocket(object socket) =>
            _isCurrentSocket(socket);

        void IInvalidateCurrentSocketHost.StopConnectionHealthMonitor(string reason) =>
            _stopHealthMonitor(reason);

        void IInvalidateCurrentSocketHost.ClearCurrentSocketField() =>
            _clearCurrentSocketField();

        void IInvalidateCurrentSocketHost.DisconnectAndDisposeSocket(object socket) =>
            _disconnectAndDispose(socket);

        void IInvalidateCurrentSocketHost.WriteSocketInvalidatedJournal(string reason) =>
            _writeJournal(reason);
    }
}
