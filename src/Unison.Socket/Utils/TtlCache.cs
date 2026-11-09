// =============================================================================
// TtlCache
//
// The small expiring cache the socket layer keeps reaching for.
//
// Baileys leans on lru-cache in half a dozen places - LID pairs, retry
// counters, recent messages, base keys - always with the same three knobs: a
// lifetime, a size cap, and whether reading an entry refreshes it. Rather than
// re-implement that inline each time, it lives here once.
//
// A full cache trims down to a low-water mark in one pass. Evicting a single
// entry per Set made every insert into a full cache an O(n log n) sort under the
// same lock readers need (26b3431b: UI Peek stalled minutes behind a listing
// import).
//
// Ports: the lru-cache usage in rc14 src/Signal/lid-mapping.ts and
// src/Utils/message-retry-manager.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Unison.Socket.Utils
{
    public sealed class TtlCache<T>
    {
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly object _gate = new object();
        private readonly TimeSpan _lifetime;
        private readonly int _maxEntries;
        private readonly int _lowWater;
        private readonly bool _refreshOnRead;
        private long _maxLockWaitTicks;

        /// <param name="maxEntries">Cap after which the entries closest to expiry are dropped.</param>
        /// <param name="refreshOnRead">
        /// When true a read restarts the entry's lifetime, so an actively used entry never expires.
        /// </param>
        public TtlCache(TimeSpan lifetime, int maxEntries = 1024, bool refreshOnRead = true)
        {
            _lifetime = lifetime;
            _maxEntries = maxEntries > 0 ? maxEntries : 1024;
            _lowWater = _maxEntries - Math.Max(1, _maxEntries / 10);
            _refreshOnRead = refreshOnRead;
        }

        public int Count
        {
            get
            {
                Enter();
                try
                {
                    return _entries.Count;
                }
                finally
                {
                    Monitor.Exit(_gate);
                }
            }
        }

        /// <summary>Longest time any caller waited to take the cache lock, in milliseconds.</summary>
        public long MaxLockWaitMs =>
            Interlocked.Read(ref _maxLockWaitTicks) * 1000 / Stopwatch.Frequency;

        public bool TryGet(string key, out T value)
        {
            value = default(T);
            if (key == null)
            {
                return false;
            }

            Enter();
            try
            {
                Entry entry;
                if (!_entries.TryGetValue(key, out entry))
                {
                    return false;
                }

                DateTime now = DateTime.UtcNow;
                if (entry.ExpiresAtUtc <= now)
                {
                    _entries.Remove(key);
                    return false;
                }

                if (_refreshOnRead)
                {
                    entry.ExpiresAtUtc = now.Add(_lifetime);
                }

                value = entry.Value;
                return true;
            }
            finally
            {
                Monitor.Exit(_gate);
            }
        }

        public T Get(string key)
        {
            T value;
            return TryGet(key, out value) ? value : default(T);
        }

        public void Set(string key, T value)
        {
            if (key == null)
            {
                return;
            }

            Enter();
            try
            {
                SetLocked(key, value, DateTime.UtcNow);
            }
            finally
            {
                Monitor.Exit(_gate);
            }
        }

        /// <summary>Stores a batch under one lock; a full cache trims at most once per low-water gap.</summary>
        public void SetMany(IEnumerable<KeyValuePair<string, T>> items)
        {
            if (items == null)
            {
                return;
            }

            Enter();
            try
            {
                DateTime now = DateTime.UtcNow;
                foreach (KeyValuePair<string, T> item in items)
                {
                    if (item.Key != null)
                    {
                        SetLocked(item.Key, item.Value, now);
                    }
                }
            }
            finally
            {
                Monitor.Exit(_gate);
            }
        }

        public bool Remove(string key)
        {
            if (key == null)
            {
                return false;
            }

            Enter();
            try
            {
                return _entries.Remove(key);
            }
            finally
            {
                Monitor.Exit(_gate);
            }
        }

        public void Clear()
        {
            Enter();
            try
            {
                _entries.Clear();
            }
            finally
            {
                Monitor.Exit(_gate);
            }
        }

        public IReadOnlyList<string> Keys
        {
            get
            {
                Enter();
                try
                {
                    return _entries.Keys.ToList();
                }
                finally
                {
                    Monitor.Exit(_gate);
                }
            }
        }

        private void SetLocked(string key, T value, DateTime now)
        {
            Entry existing;
            if (_entries.TryGetValue(key, out existing))
            {
                existing.Value = value;
                existing.ExpiresAtUtc = now.Add(_lifetime);
                return;
            }

            if (_entries.Count >= _maxEntries)
            {
                Evict(now);
            }

            _entries[key] = new Entry { Value = value, ExpiresAtUtc = now.Add(_lifetime) };
        }

        private void Enter()
        {
            if (Monitor.TryEnter(_gate))
            {
                return;
            }

            long started = Stopwatch.GetTimestamp();
            Monitor.Enter(_gate);
            long waited = Stopwatch.GetTimestamp() - started;
            long seen = Interlocked.Read(ref _maxLockWaitTicks);
            while (waited > seen)
            {
                long prior = Interlocked.CompareExchange(ref _maxLockWaitTicks, waited, seen);
                if (prior == seen)
                {
                    break;
                }

                seen = prior;
            }
        }

        /// <summary>Drops expired entries, then the ones closest to expiry down to the low-water mark.</summary>
        private void Evict(DateTime now)
        {
            List<string> expired = null;
            foreach (KeyValuePair<string, Entry> e in _entries)
            {
                if (e.Value.ExpiresAtUtc <= now)
                {
                    (expired ?? (expired = new List<string>())).Add(e.Key);
                }
            }

            if (expired != null)
            {
                foreach (string key in expired)
                {
                    _entries.Remove(key);
                }
            }

            if (_entries.Count < _maxEntries)
            {
                return;
            }

            int overflow = _entries.Count - _lowWater;
            foreach (string key in _entries.OrderBy(e => e.Value.ExpiresAtUtc).Take(overflow).Select(e => e.Key).ToList())
            {
                _entries.Remove(key);
            }
        }

        private sealed class Entry
        {
            public T Value;
            public DateTime ExpiresAtUtc;
        }
    }
}
