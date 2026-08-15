// =============================================================================
// IWaTransport
//
// The byte pipe under the socket layer: connect, send bytes, receive bytes,
// close. Everything above it (Noise, framing, binary nodes) is platform
// independent, so swapping UWP's MessageWebSocket for a test double or a future
// transport touches only the implementation of this interface.
//
// It intentionally does not reuse Unison.Core's IWhatsAppTransport: the two
// carry different EventArgs, and redefining it here avoids editing the existing
// UWP transports during the migration. An adapter bridges them.
//
// Ports: rc14 src/Socket/Client/types.ts (AbstractSocketClient)
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Unison.Socket.Abstractions
{
    /// <summary>Raw bytes read off the wire, before Noise decryption and framing.</summary>
    public sealed class WaTransportMessageEventArgs : EventArgs
    {
        public byte[] Data { get; set; }

        /// <summary>Frame replayed by the background broker rather than read live off the wire.</summary>
        public bool IsBrokerReplay { get; set; }
    }

    /// <summary>Why the transport went away. <see cref="Error"/> is null on a clean close.</summary>
    public sealed class WaTransportClosedEventArgs : EventArgs
    {
        public ushort Code { get; set; }

        public string Reason { get; set; }

        public Exception Error { get; set; }
    }

    /// <summary>
    /// Byte-level transport. The socket layer never touches a platform socket API directly;
    /// UWP supplies StreamSocket / MessageWebSocket implementations.
    /// </summary>
    public interface IWaTransport : IDisposable
    {
        string Name { get; }

        bool IsConnected { get; }

        bool IsOwnedByBroker { get; }

        string SocketId { get; }

        event Func<object, WaTransportMessageEventArgs, Task> MessageReceived;

        event EventHandler<WaTransportClosedEventArgs> Closed;

        Task ConnectAsync(Uri uri, IDictionary<string, string> headers);

        Task SendAsync(byte[] data);

        Task CloseAsync(ushort code, string reason);

        Task<bool> TransferToBrokerAsync(string reason, Func<string, Task> beforeTransfer);

        Task<bool> ReclaimFromBrokerAsync();
    }
}
