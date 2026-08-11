using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    public sealed class TransportMessageEventArgs : EventArgs
    {
        public byte[] Data { get; set; }
        public bool IsBrokerReplay { get; set; }
    }

    public sealed class TransportClosedEventArgs : EventArgs
    {
        public ushort Code { get; set; }
        public string Reason { get; set; }
        public Exception Error { get; set; }
    }

    /// <summary>
    /// WebSocket (or equivalent) transport. UWP uses StreamSocket / MessageWebSocket;
    /// other platforms supply their own implementation.
    /// </summary>
    public interface IWhatsAppTransport : IDisposable
    {
        string Name { get; }
        bool IsConnected { get; }
        bool IsOwnedByBroker { get; }
        string SocketId { get; }

        event Func<object, TransportMessageEventArgs, Task> MessageReceived;
        event EventHandler<TransportClosedEventArgs> Closed;

        Task ConnectAsync(Uri uri, IDictionary<string, string> headers);
        Task SendAsync(byte[] data);
        Task CloseAsync(ushort code, string reason);
        Task<bool> TransferToBrokerAsync(string reason, Func<string, Task> beforeTransfer);
        Task<bool> ReclaimFromBrokerAsync();
    }
}
