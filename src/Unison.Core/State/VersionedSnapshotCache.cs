// =============================================================================
// VersionedSnapshotCache
//
// RAM copy of a per-key snapshot read from storage (e.g. one group's roster),
// LRU-capped. Every write to storage invalidates its key. A reader takes a
// ticket before querying storage and stores the result with it; if a write or
// Clear happened in between, the store is dropped so the cache never holds a
// snapshot older than storage. Snapshots go in and out through a copy, so
// callers mutating what they got cannot change the cached one.
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.Core.State
{
    public sealed class VersionedSnapshotCache<T> where T : class
    {
        private sealed class Entry
        {
            public string Key;
            public T Value;
        }

        private readonly object _gate = new object();
        private readonly int _maxKeys;
        private readonly Func<T, T> _copy;
        private readonly Dictionary<string, LinkedListNode<Entry>> _entries;
        private readonly LinkedList<Entry> _recency = new LinkedList<Entry>();
        private readonly Dictionary<string, long> _writtenAt;
        private long _clock;
        private long _clearedAt;

        public VersionedSnapshotCache(int maxKeys, Func<T, T> copy, IEqualityComparer<string> comparer = null)
        {
            _maxKeys = Math.Max(1, maxKeys);
            _copy = copy ?? throw new ArgumentNullException(nameof(copy));
            _entries = new Dictionary<string, LinkedListNode<Entry>>(comparer ?? StringComparer.Ordinal);
            _writtenAt = new Dictionary<string, long>(comparer ?? StringComparer.Ordinal);
        }

        public int Count
        {
            get { lock (_gate) { return _entries.Count; } }
        }

        public bool TryGet(string key, out T value)
        {
            value = null;
            if (key == null)
            {
                return false;
            }

            lock (_gate)
            {
                LinkedListNode<Entry> node;
                if (!_entries.TryGetValue(key, out node))
                {
                    return false;
                }

                _recency.Remove(node);
                _recency.AddFirst(node);
                value = _copy(node.Value.Value);
                return true;
            }
        }

        public long BeginRead(string key)
        {
            lock (_gate)
            {
                return _clock;
            }
        }

        public void Store(string key, long ticket, T value)
        {
            if (key == null || value == null)
            {
                return;
            }

            lock (_gate)
            {
                long written;
                if (ticket < _clearedAt || (_writtenAt.TryGetValue(key, out written) && ticket < written))
                {
                    return;
                }

                RemoveEntry(key);
                var node = new LinkedListNode<Entry>(new Entry { Key = key, Value = _copy(value) });
                _recency.AddFirst(node);
                _entries[key] = node;
                while (_entries.Count > _maxKeys)
                {
                    RemoveEntry(_recency.Last.Value.Key);
                }
            }
        }

        public void Invalidate(string key)
        {
            if (key == null)
            {
                return;
            }

            lock (_gate)
            {
                _writtenAt[key] = ++_clock;
                RemoveEntry(key);
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _clearedAt = ++_clock;
                _writtenAt.Clear();
                _entries.Clear();
                _recency.Clear();
            }
        }

        private void RemoveEntry(string key)
        {
            LinkedListNode<Entry> node;
            if (_entries.TryGetValue(key, out node))
            {
                _recency.Remove(node);
                _entries.Remove(key);
            }
        }
    }
}
