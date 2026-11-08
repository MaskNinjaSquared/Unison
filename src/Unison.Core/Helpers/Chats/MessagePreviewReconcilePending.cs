// =============================================================================
// MessagePreviewReconcilePending
//
// list-chunk used to append every JID into a List (63 requests for 3 chats).
// Canonical latest-wins: one entry per chat until the worker Takes the set.
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.Core.Helpers.Chats
{
    public sealed class MessagePreviewReconcilePending
    {
        private readonly HashSet<string> _jids =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public int Count => _jids.Count;

        public bool IsEmpty => _jids.Count == 0;

        public void Add(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return;
            }

            _jids.Add(jid.Trim());
        }

        public void AddRange(IEnumerable<string> jids)
        {
            if (jids == null)
            {
                return;
            }

            foreach (string jid in jids)
            {
                Add(jid);
            }
        }

        public IReadOnlyList<string> Take()
        {
            if (_jids.Count == 0)
            {
                return Array.Empty<string>();
            }

            var batch = new List<string>(_jids);
            _jids.Clear();
            return batch;
        }
    }
}
