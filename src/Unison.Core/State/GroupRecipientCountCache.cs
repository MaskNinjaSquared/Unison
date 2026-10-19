// =============================================================================
// GroupRecipientCountCache
//
// GroupReceiptTally needs to know how many members have to agree before a
// group message turns delivered or read. Counting them costs a metadata query,
// so the answer is remembered per group for half an hour.
//
// Nothing invalidates an entry early: a member who joins or leaves inside the
// window is not counted until it expires, as before the move.
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.Core.State
{
    public sealed class GroupRecipientCountCache
    {
        public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(30);

        private sealed class Entry
        {
            public int RecipientCount { get; set; }
            public DateTime FetchedUtc { get; set; }
        }

        private readonly object _sync = new object();

        private readonly Dictionary<string, Entry> _byGroup =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        public bool TryGetFresh(string groupJid, DateTime nowUtc, out int recipientCount)
        {
            recipientCount = 0;
            if (string.IsNullOrWhiteSpace(groupJid))
            {
                return false;
            }

            lock (_sync)
            {
                if (!_byGroup.TryGetValue(groupJid, out Entry entry) ||
                    nowUtc - entry.FetchedUtc >= FreshFor)
                {
                    return false;
                }

                recipientCount = entry.RecipientCount;
                return true;
            }
        }

        public void Store(string groupJid, int recipientCount, DateTime nowUtc)
        {
            if (string.IsNullOrWhiteSpace(groupJid))
            {
                return;
            }

            lock (_sync)
            {
                _byGroup[groupJid] = new Entry
                {
                    RecipientCount = recipientCount,
                    FetchedUtc = nowUtc
                };
            }
        }
    }
}
