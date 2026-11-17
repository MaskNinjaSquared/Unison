// =============================================================================
// IByteTransport
//
// Byte duplex under SCTP (DTLS application data or an in-memory test pipe).
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Unison.Voip.Engine.Relay.Sctp
{
    public interface IByteTransport : IDisposable
    {
        void Send(byte[] data, int offset, int length);

        /// <summary>Next packet, or null when closed.</summary>
        Task<byte[]> ReceiveAsync(CancellationToken cancellationToken);
    }
}
