// =============================================================================
// ChatRowIndex
//
// Canonical address to every chat row that stands for it. A chat addressed by
// LID and the same chat addressed by phone number are two rows until they are
// merged, and nearly every message, receipt and preview asks for both.
//
// Built on demand and thrown away whenever the list or the aliases change.
// Canonicalising every row on every call was tens of millions of string
// operations on the UI thread during a sync. The owner calls Invalidate from
// the list's and the alias table's change events.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.State
{
    public sealed class ChatRowIndex
    {
        private readonly Func<IEnumerable<ChatItem>> _rows;
        private readonly Func<string, string> _canonicalOf;
        private Dictionary<string, List<ChatItem>> _byCanonical;
        private int _version;

        public ChatRowIndex(Func<IEnumerable<ChatItem>> rows, Func<string, string> canonicalOf)
        {
            _rows = rows ?? throw new ArgumentNullException(nameof(rows));
            _canonicalOf = canonicalOf ?? throw new ArgumentNullException(nameof(canonicalOf));
        }

        public void Invalidate()
        {
            Interlocked.Increment(ref _version);
            _byCanonical = null;
        }

        /// <summary>
        /// The rows filed under <paramref name="canonical"/>, as a copy: callers routinely add to
        /// or remove from the chat list while walking the result.
        /// </summary>
        public List<ChatItem> Find(string canonical)
        {
            if (string.IsNullOrWhiteSpace(canonical))
            {
                return new List<ChatItem>();
            }

            Dictionary<string, List<ChatItem>> index = _byCanonical ?? Build();

            return index.TryGetValue(canonical, out List<ChatItem> rows)
                ? new List<ChatItem>(rows)
                : new List<ChatItem>();
        }

        /// <summary>
        /// Every row that stands for the conversation at <paramref name="jid"/>, whichever alias
        /// it is given in. A copy, like <see cref="Find"/>.
        /// </summary>
        public List<ChatItem> FindConversation(string jid)
        {
            string canonical = _canonicalOf(jid);
            if (string.IsNullOrWhiteSpace(canonical))
            {
                canonical = JidHelper.Normalize(jid);
            }

            return Find(canonical);
        }

        private Dictionary<string, List<ChatItem>> Build()
        {
            int builtFrom = Volatile.Read(ref _version);
            var index = new Dictionary<string, List<ChatItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (ChatItem chat in _rows())
            {
                if (chat == null)
                {
                    continue;
                }

                string key = _canonicalOf(chat.JID);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (!index.TryGetValue(key, out List<ChatItem> bucket))
                {
                    bucket = new List<ChatItem>();
                    index[key] = bucket;
                }

                bucket.Add(chat);
            }

            // Kept only if nothing changed while it was being built. A build that raced an
            // invalidation describes a list that no longer exists, and it would stay installed.
            if (Volatile.Read(ref _version) == builtFrom)
            {
                _byCanonical = index;
            }

            return index;
        }
    }
}
