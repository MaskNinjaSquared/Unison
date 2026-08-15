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
// Ports: the lru-cache usage in rc14 src/Signal/lid-mapping.ts and
// src/Utils/message-retry-manager.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace Unison.Socket.Utils
{
    public sealed class TtlCache<T>
    {
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly object _gate = new object();
        private readonly TimeSpan _lifetime;
        private readonly int _maxEntries;
        private readonly bool _refreshOnRead;

        /// <param name="maxEntries">Cap after which the entries closest to expiry are dropped.</param>
        /// <param name="refreshOnRead">
        /// When true a read restarts the entry's lifetime, so an actively used entry never expires.
        /// </param>
        public TtlCache(TimeSpan lifetime, int maxEntries = 1024, bool refreshOnRead = true)
        {
            _lifetime = lifetime;
            _maxEntries = maxEntries > 0 ? maxEntries : 1024;
            _refreshOnRead = refreshOnRead;
        }

        public bool TryGet(string key, out T value)
        {
            value = default(T);
            if (key == null)
            {
                return false;
            }

            lock (_gate)
            {
                Entry entry;
                if (!_entries.TryGetValue(key, out entry))
                {
                    return false;
                }

                if (entry.ExpiresAtUtc <= DateTime.UtcNow)
                {
                    _entries.Remove(key);
                    return false;
                }

                if (_refreshOnRead)
                {
                    entry.ExpiresAtUtc = DateTime.UtcNow.Add(_lifetime);
                }

                value = entry.Value;
                return true;
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

            lock (_gate)
            {
                if (!_entries.ContainsKey(key) && _entries.Count >= _maxEntries)
                {
                    Evict();
                }

                _entries[key] = new Entry { Value = value, ExpiresAtUtc = DateTime.UtcNow.Add(_lifetime) };
            }
        }

        public bool Remove(string key)
        {
            if (key == null)
            {
                return false;
            }

            lock (_gate)
            {
                return _entries.Remove(key);
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _entries.Clear();
            }
        }

        public IReadOnlyList<string> Keys
        {
            get
            {
                lock (_gate)
                {
                    return _entries.Keys.ToList();
                }
            }
        }

        /// <summary>Drops expired entries, then the oldest ones if the cap is still exceeded.</summary>
        private void Evict()
        {
            var now = DateTime.UtcNow;
            foreach (var key in _entries.Where(e => e.Value.ExpiresAtUtc <= now).Select(e => e.Key).ToList())
            {
                _entries.Remove(key);
            }

            if (_entries.Count < _maxEntries)
            {
                return;
            }

            var overflow = _entries.Count - _maxEntries + 1;
            foreach (var key in _entries.OrderBy(e => e.Value.ExpiresAtUtc).Take(overflow).Select(e => e.Key).ToList())
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
