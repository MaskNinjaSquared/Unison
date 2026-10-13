using System;
using System.Collections.Generic;
using Unison.Core.Mappers;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Inserts and compares timeline rows by protocol timestamp, then id —
    /// the same order <c>MessageStore</c> persists. Arrival order is not used.
    /// </summary>
    public static class ChatMessageOrder
    {
        public static void InsertSorted(IList<ChatMessage> list, ChatMessage message)
        {
            if (list == null || message == null)
            {
                return;
            }

            list.Insert(FindInsertIndex(list, message.Timestamp, message.Id), message);
        }

        public static void SortInPlace(List<ChatMessage> list)
        {
            if (list == null || list.Count < 2)
            {
                return;
            }

            list.Sort(CompareMessages);
        }

        public static int FindInsertIndex(IList<ChatMessage> list, DateTime timestamp, string id)
        {
            if (list == null || list.Count == 0)
            {
                return 0;
            }

            return FindInsertIndex(
                list.Count,
                i => list[i]?.Timestamp ?? DateTime.MinValue,
                i => list[i]?.Id,
                timestamp,
                id);
        }

        public static int FindInsertIndex(
            int count,
            Func<int, DateTime> getTimestamp,
            Func<int, string> getId,
            DateTime timestamp,
            string id)
        {
            if (count <= 0 || getTimestamp == null)
            {
                return 0;
            }

            DateTime utc = ToComparableUtc(timestamp);
            string key = id ?? string.Empty;
            int lo = 0;
            int hi = count;
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) / 2);
                DateTime midUtc = ToComparableUtc(getTimestamp(mid));
                string midId = getId != null ? getId(mid) : null;
                if (Compare(midUtc, midId, utc, key) <= 0)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            return lo;
        }

        /// <summary>
        /// Same rules as <see cref="WhatsAppMapper.ToUtc"/>: SQLite
        /// <see cref="DateTimeKind.Unspecified"/> is already UTC wall-clock — do not
        /// call <see cref="DateTime.ToUniversalTime"/> (that treats Unspecified as local
        /// and shifts Brazil UTC−3 by +3h into the strip).
        /// </summary>
        public static DateTime ToComparableUtc(DateTime timestamp)
        {
            if (timestamp == DateTime.MinValue || timestamp == DateTime.MaxValue)
            {
                return timestamp;
            }

            return WhatsAppMapper.ToUtc(timestamp);
        }

        /// <summary>
        /// The newest timestamp in a set of messages, normalized for comparison, or
        /// <see cref="DateTime.MinValue"/> when there is nothing to read.
        /// </summary>
        /// <remarks>
        /// Exists so callers stop reaching for <c>Max(m =&gt; m.Timestamp)</c>, which compares
        /// raw values. A list holding both live messages and rows read back from SQLite holds
        /// both <see cref="DateTimeKind.Utc"/> and <see cref="DateTimeKind.Unspecified"/>, and
        /// comparing those directly is off by the local offset.
        /// </remarks>
        public static DateTime NewestComparableUtc(IEnumerable<ChatMessage> messages)
        {
            DateTime newest = DateTime.MinValue;
            if (messages == null)
            {
                return newest;
            }

            foreach (ChatMessage message in messages)
            {
                if (message == null)
                {
                    continue;
                }

                DateTime candidate = ToComparableUtc(message.Timestamp);
                if (candidate > newest)
                {
                    newest = candidate;
                }
            }

            return newest;
        }

        private static int CompareMessages(ChatMessage left, ChatMessage right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left == null)
            {
                return 1;
            }

            if (right == null)
            {
                return -1;
            }

            return Compare(
                ToComparableUtc(left.Timestamp),
                left.Id,
                ToComparableUtc(right.Timestamp),
                right.Id);
        }

        private static int Compare(DateTime utcA, string idA, DateTime utcB, string idB)
        {
            int time = utcA.CompareTo(utcB);
            if (time != 0)
            {
                return time;
            }

            return string.Compare(idA ?? string.Empty, idB ?? string.Empty, StringComparison.Ordinal);
        }
    }
}
