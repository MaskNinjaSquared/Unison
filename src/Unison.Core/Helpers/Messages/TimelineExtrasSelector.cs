using System;
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Picks the rows a timeline's first page must carry even when they fall outside it:
    /// unexpired pins (newest pin first, capped) then pending / failed outgoing (oldest first).
    /// Input is one SQLite read over every alias key of the chat, so the same message id can
    /// arrive more than once; it is kept once.
    /// </summary>
    public static class TimelineExtrasSelector
    {
        public static IReadOnlyList<HistoryMessage> Select(
            IReadOnlyList<HistoryMessage> candidates,
            int maxPinned,
            DateTime nowUtc)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return Array.Empty<HistoryMessage>();
            }

            var pins = new List<HistoryMessage>();
            var pending = new List<HistoryMessage>();
            for (int i = 0; i < candidates.Count; i++)
            {
                HistoryMessage row = candidates[i];
                if (row == null || string.IsNullOrWhiteSpace(row.MessageId))
                {
                    continue;
                }

                if (IsLivePin(row, nowUtc))
                {
                    pins.Add(row);
                }
                else if (IsUnsentOutgoing(row))
                {
                    pending.Add(row);
                }
            }

            pins.Sort((a, b) => Nullable.Compare(b.PinnedAtUtc, a.PinnedAtUtc));
            pending.Sort((a, b) => Nullable.Compare(a.TimestampUtc, b.TimestampUtc));

            int take = Math.Max(1, maxPinned);
            var picked = new List<HistoryMessage>(Math.Min(pins.Count, take) + pending.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < pins.Count && seen.Count < take; i++)
            {
                if (seen.Add(pins[i].MessageId))
                {
                    picked.Add(pins[i]);
                }
            }

            for (int i = 0; i < pending.Count; i++)
            {
                if (seen.Add(pending[i].MessageId))
                {
                    picked.Add(pending[i]);
                }
            }

            return picked;
        }

        private static bool IsLivePin(HistoryMessage row, DateTime nowUtc) =>
            row.IsPinned && !(row.PinExpiresAtUtc.HasValue && row.PinExpiresAtUtc.Value <= nowUtc);

        private static bool IsUnsentOutgoing(HistoryMessage row) =>
            row.IsFromMe &&
            (row.SendState == MessageSendState.Pending || row.SendState == MessageSendState.Failed);
    }
}
