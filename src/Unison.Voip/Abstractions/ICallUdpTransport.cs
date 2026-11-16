using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Unison.Voip.Abstractions
{
    /// <summary>
    /// Datagram I/O for WA relay / STUN (UWP <c>DatagramSocket</c>). Byte pipe only — no WinRT.
    /// </summary>
    public interface ICallUdpTransport : IDisposable
    {
        Task BindAsync(CancellationToken cancellationToken);

        Task SendAsync(byte[] payload, IPEndPoint remote, CancellationToken cancellationToken);

        /// <summary>Next datagram, or null when the transport is closed.</summary>
        Task<CallUdpDatagram> ReceiveAsync(CancellationToken cancellationToken);
    }

    public sealed class CallUdpDatagram
    {
        public byte[] Payload { get; set; }

        public IPEndPoint Remote { get; set; }
    }
}
