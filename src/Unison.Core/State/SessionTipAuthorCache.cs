// =============================================================================
// SessionTipAuthorCache
//
// Session map of tip-author jid → display name for sync loops (offline tip
// prepare, later chunk reuse). Miss resolves once; PutOrUpdate replaces when a
// newer name arrives so the next tip paint and ChatAuthorProjection stay aligned.
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.Core.State
{
    public sealed class SessionTipAuthorCache
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, string> _names =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public bool TryGet(string jid, out string name)
        {
            name = null;
            if (string.IsNullOrWhiteSpace(jid))
            {
                return false;
            }

            lock (_gate)
            {
                string stored;
                if (!_names.TryGetValue(jid.Trim(), out stored) || string.IsNullOrWhiteSpace(stored))
                {
                    return false;
                }

                name = stored;
                return true;
            }
        }

        /// <summary>
        /// Stores <paramref name="name"/>. Returns true when the entry is new or the
        /// text changed (caller may refresh the list strip).
        /// </summary>
        public bool PutOrUpdate(string jid, string name)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return false;
            }

            string key = jid.Trim();
            string next = string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();
            lock (_gate)
            {
                string previous;
                if (_names.TryGetValue(key, out previous) &&
                    string.Equals(previous, next, StringComparison.Ordinal))
                {
                    return false;
                }

                _names[key] = next;
                return true;
            }
        }

        public void Invalidate(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return;
            }

            lock (_gate)
            {
                _names.Remove(jid.Trim());
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _names.Clear();
            }
        }
    }
}
