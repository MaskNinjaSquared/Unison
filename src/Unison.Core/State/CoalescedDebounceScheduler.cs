// =============================================================================
// CoalescedDebounceScheduler
//
// Arms a single delayed callback and cancels the previous arm when another
// request arrives in the window. Same seam as PersistScheduler: the host still
// owns what the callback does; this owns only cancel-and-rearm.
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Unison.Core.State
{
    /// <summary>
    /// Coalesces a burst of requests into one callback after <see cref="Debounce"/>.
    /// </summary>
    public sealed class CoalescedDebounceScheduler : IDisposable
    {
        private readonly TimeSpan _debounce;
        private readonly object _gate = new object();
        private CancellationTokenSource _cts;
        private bool _disposed;

        public CoalescedDebounceScheduler(TimeSpan debounce)
        {
            if (debounce < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(debounce));
            }

            _debounce = debounce;
        }

        public TimeSpan Debounce => _debounce;

        /// <summary>
        /// Cancels any pending arm and schedules <paramref name="callback"/> after the debounce.
        /// </summary>
        public void Schedule(Action callback)
        {
            if (callback == null)
            {
                throw new ArgumentNullException(nameof(callback));
            }

            CancellationToken token;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (_cts != null)
                {
                    _cts.Cancel();
                    _cts.Dispose();
                }

                _cts = new CancellationTokenSource();
                token = _cts.Token;
            }

            Task.Delay(_debounce, token).ContinueWith(
                t =>
                {
                    if (t.IsCanceled)
                    {
                        return;
                    }

                    callback();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        /// <summary>Drops a pending arm without running the callback.</summary>
        public void Cancel()
        {
            lock (_gate)
            {
                if (_cts == null)
                {
                    return;
                }

                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                if (_cts != null)
                {
                    _cts.Cancel();
                    _cts.Dispose();
                    _cts = null;
                }
            }
        }
    }
}
