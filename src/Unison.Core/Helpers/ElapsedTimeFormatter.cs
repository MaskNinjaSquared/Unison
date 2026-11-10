// =============================================================================
// ElapsedTimeFormatter
//
// Localized "x minutes ago" / "x hours ago" for short-lived items (Status posts).
// =============================================================================
using System;
using System.Globalization;
using Unison.Core.Contracts;

namespace Unison.Core.Helpers
{
    public static class ElapsedTimeFormatter
    {
        public static string Format(DateTime? timestampUtc, DateTime nowUtc, IStringResources strings)
        {
            if (!timestampUtc.HasValue)
            {
                return string.Empty;
            }

            DateTime then = timestampUtc.Value.Kind == DateTimeKind.Local
                ? timestampUtc.Value.ToUniversalTime()
                : DateTime.SpecifyKind(timestampUtc.Value, DateTimeKind.Utc);
            TimeSpan elapsed = nowUtc - then;

            if (elapsed.TotalMinutes < 1)
            {
                return Get(strings, "Time_JustNow", "Just now");
            }

            if (elapsed.TotalHours < 1)
            {
                int minutes = (int)elapsed.TotalMinutes;
                return minutes == 1
                    ? Get(strings, "Time_OneMinuteAgo", "1 minute ago")
                    : Fill(Get(strings, "Time_MinutesAgo", "{0} minutes ago"), minutes);
            }

            int hours = (int)elapsed.TotalHours;
            return hours == 1
                ? Get(strings, "Time_OneHourAgo", "1 hour ago")
                : Fill(Get(strings, "Time_HoursAgo", "{0} hours ago"), hours);
        }

        private static string Get(IStringResources strings, string key, string fallback) =>
            strings?.Get(key, fallback) ?? fallback;

        private static string Fill(string template, int value) =>
            string.Format(CultureInfo.CurrentCulture, template, value.ToString(CultureInfo.CurrentCulture));
    }
}
