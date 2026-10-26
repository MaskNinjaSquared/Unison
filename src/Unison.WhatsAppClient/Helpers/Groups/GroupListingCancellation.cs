// =============================================================================
// GroupListingCancellation
//
// Owns the CTS for participating-groups listing + unresolved-metadata passes.
// Visibility hide Cancels the in-flight waiters; a new Begin() arms the next
// pass. Wire Query* take the Token so ConnectionHandler can drop the IQ waiter.
// =============================================================================
using System;
using System.Threading;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class GroupListingCancellation : IDisposable
    {
        private readonly object _gate = new object();
        private CancellationTokenSource _cts;

        public CancellationToken Token
        {
            get
            {
                lock (_gate)
                {
                    return _cts != null ? _cts.Token : CancellationToken.None;
                }
            }
        }

        public bool IsCancellationRequested => Token.IsCancellationRequested;

        /// <summary>Cancels any prior pass and returns a fresh token for this listing.</summary>
        public CancellationToken Begin()
        {
            lock (_gate)
            {
                CancelUnlocked();
                _cts = new CancellationTokenSource();
                return _cts.Token;
            }
        }

        /// <summary>Signals abort (window hidden / shutdown). No-op when idle.</summary>
        public void Cancel()
        {
            lock (_gate)
            {
                CancelUnlocked();
            }
        }

        public void Dispose() => Cancel();

        private void CancelUnlocked()
        {
            CancellationTokenSource current = _cts;
            _cts = null;
            if (current == null)
            {
                return;
            }

            try
            {
                current.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                current.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
