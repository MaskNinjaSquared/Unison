// =============================================================================
// CallHistoryDateGrouping
//
// Buckets call history by local calendar day for SemanticZoom headers / jump.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Unison.Core.Mappers;
using Unison.Core.Models;

namespace Unison.Core.Helpers.Calls
{
    public static class CallHistoryDateGrouping
    {
        public static IReadOnlyList<CallHistoryDateGroup> Build(
            IEnumerable<CallHistoryEntry> entries,
            string todayLabel,
            string yesterdayLabel)
        {
            if (entries == null)
            {
                return Array.Empty<CallHistoryDateGroup>();
            }

            string today = string.IsNullOrWhiteSpace(todayLabel) ? "Today" : todayLabel.Trim();
            string yesterday = string.IsNullOrWhiteSpace(yesterdayLabel) ? "Yesterday" : yesterdayLabel.Trim();

            var buckets = new Dictionary<DateTime, List<CallHistoryEntry>>();
            foreach (CallHistoryEntry entry in entries)
            {
                if (entry == null)
                {
                    continue;
                }

                DateTime day = WhatsAppMapper.ToLocalCalendarDate(entry.TimestampUtc);
                if (day == DateTime.MinValue)
                {
                    day = DateTime.Today;
                }

                List<CallHistoryEntry> list;
                if (!buckets.TryGetValue(day, out list))
                {
                    list = new List<CallHistoryEntry>();
                    buckets[day] = list;
                }

                list.Add(entry);
            }

            return buckets
                .OrderByDescending(kv => kv.Key)
                .Select(kv => new CallHistoryDateGroup(
                    FormatKey(kv.Key, today, yesterday),
                    kv.Key,
                    kv.Value.OrderByDescending(e => e.TimestampUtc)))
                .ToList();
        }

        private static string FormatKey(DateTime day, string today, string yesterday)
        {
            // Noon avoids DST edge cases when FormatDaySeparator converts again.
            DateTime stamp = DateTime.SpecifyKind(day.AddHours(12), DateTimeKind.Local);
            return WhatsAppMapper.FormatDaySeparator(stamp, today, yesterday);
        }
    }
}
