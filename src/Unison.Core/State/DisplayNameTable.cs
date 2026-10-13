using System;
using System.Collections;
using System.Collections.Generic;

namespace Unison.Core.State
{
    /// <summary>
    /// JID → display name, keyed case-insensitively, safe to read and write from different threads.
    /// </summary>
    /// <remarks>
    /// The push-name and address-book maps used to be bare dictionaries on <see cref="ChatStateStore"/>.
    /// The address-book reader writes after an awaited contact query, off the UI thread, while the chat
    /// list reads the same map on it; a dictionary read during a resize can loop or throw. Enumeration
    /// walks a copy, so a <c>foreach</c> never sees a write land mid-walk.
    /// </remarks>
    public sealed class DisplayNameTable : IReadOnlyDictionary<string, string>
    {
        private readonly object _gate = new object();

        private readonly Dictionary<string, string> _names =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _names.Count;
                }
            }
        }

        public string this[string jid]
        {
            get
            {
                string name;
                if (!TryGetValue(jid, out name))
                {
                    throw new KeyNotFoundException(jid);
                }

                return name;
            }
        }

        public IEnumerable<string> Keys
        {
            get { return Snapshot().Keys; }
        }

        public IEnumerable<string> Values
        {
            get { return Snapshot().Values; }
        }

        public bool ContainsKey(string jid)
        {
            if (jid == null)
            {
                return false;
            }

            lock (_gate)
            {
                return _names.ContainsKey(jid);
            }
        }

        public bool TryGetValue(string jid, out string name)
        {
            if (jid == null)
            {
                name = null;
                return false;
            }

            lock (_gate)
            {
                return _names.TryGetValue(jid, out name);
            }
        }

        /// <summary>Stores the name. Returns false when it was already exactly that.</summary>
        public bool Set(string jid, string name)
        {
            if (jid == null)
            {
                throw new ArgumentNullException(nameof(jid));
            }

            lock (_gate)
            {
                string current;
                if (_names.TryGetValue(jid, out current) && string.Equals(current, name, StringComparison.Ordinal))
                {
                    return false;
                }

                _names[jid] = name;
                return true;
            }
        }

        /// <summary>Stores the name only when the JID has none yet.</summary>
        public bool TryAdd(string jid, string name)
        {
            if (jid == null)
            {
                throw new ArgumentNullException(nameof(jid));
            }

            lock (_gate)
            {
                if (_names.ContainsKey(jid))
                {
                    return false;
                }

                _names[jid] = name;
                return true;
            }
        }

        public bool Remove(string jid)
        {
            if (jid == null)
            {
                return false;
            }

            lock (_gate)
            {
                return _names.Remove(jid);
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _names.Clear();
            }
        }

        /// <summary>A detached copy with the same case-insensitive keys.</summary>
        public Dictionary<string, string> Snapshot()
        {
            lock (_gate)
            {
                return new Dictionary<string, string>(_names, StringComparer.OrdinalIgnoreCase);
            }
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            return Snapshot().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
