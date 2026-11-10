// =============================================================================
// OfflineSyncedWatchdog
//
// Per-open belt: if offline-replay-release never arrives, force a release so
// status can move open → synced and the Updating… banner can clear.
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class OfflineSyncedWatchdog
    {
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

        private readonly Func<bool> _isTransportReady;
        private readonly Func<bool> _hasReleased;
        private readonly Action<int> _onFire;
        private readonly Action<string> _journal;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly TimeSpan _timeout;

        private readonly object _gate = new object();
        private int _generation;
        private CancellationTokenSource _cts;

        public OfflineSyncedWatchdog(
            Func<bool> isTransportReady,
            Func<bool> hasReleased,
            Action<int> onFire,
            Action<string> journal,
            Func<TimeSpan, CancellationToken, Task> delay = null,
            TimeSpan? timeout = null)
        {
            _isTransportReady = isTransportReady
                ?? throw new ArgumentNullException(nameof(isTransportReady));
            _hasReleased = hasReleased
                ?? throw new ArgumentNullException(nameof(hasReleased));
            _onFire = onFire ?? throw new ArgumentNullException(nameof(onFire));
            _journal = journal ?? throw new ArgumentNullException(nameof(journal));
            _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
            _timeout = timeout ?? DefaultTimeout;
        }

        public void Arm()
        {
            int generation;
            CancellationToken token;
            lock (_gate)
            {
                CancelLocked();
                generation = ++_generation;
                _cts = new CancellationTokenSource();
                token = _cts.Token;
            }

            _journal("armed|generation=" + generation);
            var _ = RunAsync(generation, token);
        }

        public void Cancel(string reason)
        {
            lock (_gate)
            {
                CancelLocked();
            }

            _journal("cancelled|reason=" + (reason ?? string.Empty));
        }

        private void CancelLocked()
        {
            var cts = _cts;
            _cts = null;
            if (cts == null)
            {
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            cts.Dispose();
        }

        private async Task RunAsync(int generation, CancellationToken token)
        {
            try
            {
                await _delay(_timeout, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lock (_gate)
            {
                if (generation != _generation || token.IsCancellationRequested)
                {
                    return;
                }
            }

            if (_hasReleased())
            {
                _journal("skipped-already-released|generation=" + generation);
                return;
            }

            if (!_isTransportReady())
            {
                _journal("skipped-transport|generation=" + generation);
                return;
            }

            _journal("fired|generation=" + generation);
            _onFire(generation);
        }
    }
}
