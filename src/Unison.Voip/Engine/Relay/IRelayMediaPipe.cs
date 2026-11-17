// =============================================================================
// IRelayMediaPipe
//
// Send/recv over the open relay DataChannel. RelayMediaChannel implements it;
// tests use a recording fake.
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Unison.Voip.Engine.Relay
{
    public interface IRelayMediaPipe : IDisposable
    {
        Task SendAsync(byte[] data, CancellationToken cancellationToken);

        /// <summary>Next DataChannel message, or null when closed.</summary>
        Task<byte[]> RecvAsync(CancellationToken cancellationToken);
    }
}
