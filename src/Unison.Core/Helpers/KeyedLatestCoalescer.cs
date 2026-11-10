// =============================================================================
// KeyedLatestCoalescer
//
// Folds a storm of per-key updates (receipts, presence, chat flags) into one
// batch per window, keeping one value per key. The first post arms the window;
// later posts join it without pushing the flush back, so latency is bounded by
// one window. Each post's task completes when the batch carrying it is applied.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Unison.Core.Helpers
{
    public sealed class KeyedLatestCoalescer<TKey, TValue>
    {
        private readonly Func<IReadOnlyList<KeyValuePair<TKey, TValue>>, Task> _flush;
        private readonly Func<TimeSpan> _window;
        private readonly Func<TValue, TValue, TValue> _merge;
        private readonly Func<TimeSpan, Task> _delay;
        private readonly IEqualityComparer<TKey> _comparer;
        private readonly object _gate = new object();
        private readonly SemaphoreSlim _flushGate = new SemaphoreSlim(1, 1);

        private Dictionary<TKey, int> _index;
        private List<KeyValuePair<TKey, TValue>> _pending;
        private TaskCompletionSource<bool> _batchDone;
        private int _generation;
        private bool _armed;

        /// <param name="merge">(pending, incoming) → survivor. Null: latest wins.</param>
        /// <param name="delay">Test seam; production uses <see cref="Task.Delay(TimeSpan)"/>.</param>
        public KeyedLatestCoalescer(
            Func<IReadOnlyList<KeyValuePair<TKey, TValue>>, Task> flush,
            Func<TimeSpan> window,
            Func<TValue, TValue, TValue> merge = null,
            IEqualityComparer<TKey> comparer = null,
            Func<TimeSpan, Task> delay = null)
        {
            _flush = flush ?? throw new ArgumentNullException(nameof(flush));
            _window = window ?? throw new ArgumentNullException(nameof(window));
            _merge = merge ?? ((pending, incoming) => incoming);
            _comparer = comparer ?? EqualityComparer<TKey>.Default;
            _delay = delay ?? Task.Delay;
            ResetPendingLocked();
        }

        public int PendingCount
        {
            get { lock (_gate) { return _pending.Count; } }
        }

        /// <summary>Queues <paramref name="value"/> for <paramref name="key"/>; completes when its batch is applied.</summary>
        public Task Post(TKey key, TValue value)
        {
            Task done;
            int generation = -1;
            lock (_gate)
            {
                int at;
                if (_index.TryGetValue(key, out at))
                {
                    KeyValuePair<TKey, TValue> current = _pending[at];
                    _pending[at] = new KeyValuePair<TKey, TValue>(current.Key, _merge(current.Value, value));
                }
                else
                {
                    _index[key] = _pending.Count;
                    _pending.Add(new KeyValuePair<TKey, TValue>(key, value));
                }

                done = _batchDone.Task;
                if (!_armed)
                {
                    _armed = true;
                    generation = _generation;
                }
            }

            if (generation >= 0)
            {
                _ = FlushAfterWindowAsync(generation);
            }

            return done;
        }

        /// <summary>Applies whatever is pending now (shutdown, tests, a caller that cannot wait).</summary>
        public Task FlushNowAsync() => FlushPendingAsync(expectedGeneration: null);

        private async Task FlushAfterWindowAsync(int generation)
        {
            TimeSpan window;
            try
            {
                window = _window();
            }
            catch (Exception ex)
            {
                // A broken window read must not strand the batch: apply now.
                Debug.WriteLine("[KeyedLatestCoalescer] window read failed: " + ex.Message);
                window = TimeSpan.Zero;
            }

            if (window > TimeSpan.Zero)
            {
                await _delay(window).ConfigureAwait(false);
            }

            await FlushPendingAsync(generation).ConfigureAwait(false);
        }

        private async Task FlushPendingAsync(int? expectedGeneration)
        {
            List<KeyValuePair<TKey, TValue>> batch;
            TaskCompletionSource<bool> done;
            lock (_gate)
            {
                // A FlushNowAsync already took this window's batch; the late timer has nothing to do.
                if (expectedGeneration.HasValue && expectedGeneration.Value != _generation)
                {
                    return;
                }

                if (_pending.Count == 0)
                {
                    _armed = false;
                    return;
                }

                batch = _pending;
                done = _batchDone;
                _generation++;
                _armed = false;
                ResetPendingLocked();
            }

            // Serialised so batch N+1 never lands before batch N (a late "delivered" over "read").
            await _flushGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _flush(batch).ConfigureAwait(false);
                done.TrySetResult(true);
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
            finally
            {
                _flushGate.Release();
            }
        }

        private void ResetPendingLocked()
        {
            _index = new Dictionary<TKey, int>(_comparer);
            _pending = new List<KeyValuePair<TKey, TValue>>();
            _batchDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
