// =============================================================================
// UsyncGate
//
// A semaphore of one, handed out as a lease. Extracted from WhatsAppService's
// private _usyncLock so that contacts and avatars can keep sharing it while
// they move to separate owners.
//
// The lease exists because the alternative is a WaitAsync / lockTaken / Release
// triple at every call site, and one of those sites already had to swallow
// ObjectDisposedException and SemaphoreFullException to stay safe.
// =============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;

namespace Unison.Uwp.Services
{
    public sealed class UsyncGate : IUsyncGate
    {
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public async Task<IDisposable> AcquireAsync(CancellationToken token = default(CancellationToken))
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            return new Lease(_gate);
        }

        /// <summary>
        /// Releasing twice would let a second caller through while the first is still on the wire,
        /// so the lease goes inert after the first dispose rather than trusting callers not to.
        /// </summary>
        private sealed class Lease : IDisposable
        {
            private SemaphoreSlim _gate;

            internal Lease(SemaphoreSlim gate)
            {
                _gate = gate;
            }

            public void Dispose()
            {
                var gate = Interlocked.Exchange(ref _gate, null);
                if (gate == null)
                {
                    return;
                }

                try
                {
                    gate.Release();
                }
                catch (ObjectDisposedException)
                {
                }
                catch (SemaphoreFullException ex)
                {
                    Debug.WriteLine("[UsyncGate] Release past capacity: " + ex.Message);
                }
            }
        }
    }
}
