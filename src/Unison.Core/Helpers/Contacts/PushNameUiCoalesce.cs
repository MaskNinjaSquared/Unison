// =============================================================================
// PushNameUiCoalesce
//
// When notify= changes, each ApplyStanzaNotify used to schedule its own Normal
// hop. Latest-wins per canonical JID until the host drains the map.
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.Core.Helpers.Contacts
{
    public sealed class PushNameUiCoalesce
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, string> _pending =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public int Count
        {
            get { lock (_gate) { return _pending.Count; } }
        }

        public void Note(string canonicalJid, string displayName)
        {
            if (string.IsNullOrWhiteSpace(canonicalJid) || string.IsNullOrWhiteSpace(displayName))
            {
                return;
            }

            lock (_gate)
            {
                _pending[canonicalJid.Trim()] = displayName;
            }
        }

        public IReadOnlyList<KeyValuePair<string, string>> Take()
        {
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    return Array.Empty<KeyValuePair<string, string>>();
                }

                var batch = new List<KeyValuePair<string, string>>(_pending);
                _pending.Clear();
                return batch;
            }
        }
    }
}
