// =============================================================================
// AsyncQueryCache
//
// Answers to a slow query (an IQ) kept per key, with three rules: the answer
// decides its own TTL (null = do not keep), concurrent readers of one key share
// one query, and a revalidating read bypasses the cache and writes the fresh
// answer back - at most once per key per cooldown, so reopening the same chat
// does not repeat the query.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Unison.Socket.Utils
{
    public sealed class AsyncQueryCache<T> where T : class
    {
        private readonly Func<T, TimeSpan?> _ttlFor;
        private readonly TimeSpan _revalidateCooldown;
        private readonly int _maxEntries;
        private readonly Func<TimeSpan> _clock;
        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Dictionary<string, Task<T>> _inFlight = new Dictionary<string, Task<T>>(StringComparer.Ordinal);
        private readonly Dictionary<string, TimeSpan> _revalidatedAt = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);

        /// <param name="ttlFor">How long an answer stays; null keeps nothing (timeouts, transient errors).</param>
        /// <param name="clock">Test seam; production uses a monotonic <see cref="Stopwatch"/>.</param>
        public AsyncQueryCache(
            Func<T, TimeSpan?> ttlFor,
            TimeSpan revalidateCooldown,
            int maxEntries,
            Func<TimeSpan> clock = null)
        {
            _ttlFor = ttlFor ?? throw new ArgumentNullException(nameof(ttlFor));
            _revalidateCooldown = revalidateCooldown;
            _maxEntries = Math.Max(1, maxEntries);
            if (clock == null)
            {
                Stopwatch watch = Stopwatch.StartNew();
                clock = () => watch.Elapsed;
            }

            _clock = clock;
        }

        public int Count
        {
            get { lock (_gate) { return _entries.Count; } }
        }

        public Task<T> GetAsync(string key, bool revalidate, Func<Task<T>> query)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            if (query == null)
            {
                throw new ArgumentNullException(nameof(query));
            }

            TaskCompletionSource<T> answer;
            lock (_gate)
            {
                TimeSpan now = _clock();
                bool bypass = revalidate && !RevalidatedRecentlyLocked(key, now);
                Entry entry;
                if (!bypass && _entries.TryGetValue(key, out entry))
                {
                    if (entry.ExpiresAt > now)
                    {
                        return Task.FromResult(entry.Value);
                    }

                    _entries.Remove(key);
                }

                Task<T> running;
                if (_inFlight.TryGetValue(key, out running))
                {
                    return running;
                }

                if (bypass)
                {
                    _revalidatedAt[key] = now;
                }

                answer = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                _inFlight[key] = answer.Task;
            }

            _ = RunAsync(key, query, answer);
            return answer.Task;
        }

        public void Invalidate(string key)
        {
            if (key == null)
            {
                return;
            }

            lock (_gate)
            {
                _entries.Remove(key);
            }
        }

        private async Task RunAsync(string key, Func<Task<T>> query, TaskCompletionSource<T> answer)
        {
            T value;
            try
            {
                value = await query().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _inFlight.Remove(key);
                }

                answer.TrySetException(ex);
                return;
            }

            lock (_gate)
            {
                _inFlight.Remove(key);
                TimeSpan? ttl = value != null ? _ttlFor(value) : null;
                if (ttl.HasValue && ttl.Value > TimeSpan.Zero)
                {
                    TimeSpan now = _clock();
                    _entries[key] = new Entry(value, now + ttl.Value, now);
                    TrimLocked(now);
                }
            }

            answer.TrySetResult(value);
        }

        private bool RevalidatedRecentlyLocked(string key, TimeSpan now)
        {
            TimeSpan at;
            return _revalidatedAt.TryGetValue(key, out at) && now - at < _revalidateCooldown;
        }

        private void TrimLocked(TimeSpan now)
        {
            if (_entries.Count <= _maxEntries)
            {
                return;
            }

            var expired = new List<string>();
            foreach (KeyValuePair<string, Entry> pair in _entries)
            {
                if (pair.Value.ExpiresAt <= now)
                {
                    expired.Add(pair.Key);
                }
            }

            for (int i = 0; i < expired.Count; i++)
            {
                _entries.Remove(expired[i]);
            }

            if (_entries.Count <= _maxEntries)
            {
                return;
            }

            // Oldest stored first, down to 90% so the next inserts do not trim again.
            int target = _maxEntries - Math.Max(1, _maxEntries / 10);
            var byAge = new List<KeyValuePair<string, Entry>>(_entries);
            byAge.Sort((a, b) => a.Value.StoredAt.CompareTo(b.Value.StoredAt));
            for (int i = 0; i < byAge.Count && _entries.Count > target; i++)
            {
                _entries.Remove(byAge[i].Key);
            }

            PruneRevalidatedLocked(now);
        }

        private void PruneRevalidatedLocked(TimeSpan now)
        {
            var stale = new List<string>();
            foreach (KeyValuePair<string, TimeSpan> pair in _revalidatedAt)
            {
                if (now - pair.Value >= _revalidateCooldown)
                {
                    stale.Add(pair.Key);
                }
            }

            for (int i = 0; i < stale.Count; i++)
            {
                _revalidatedAt.Remove(stale[i]);
            }
        }

        private struct Entry
        {
            public Entry(T value, TimeSpan expiresAt, TimeSpan storedAt)
            {
                Value = value;
                ExpiresAt = expiresAt;
                StoredAt = storedAt;
            }

            public T Value { get; }

            public TimeSpan ExpiresAt { get; }

            public TimeSpan StoredAt { get; }
        }
    }
}
