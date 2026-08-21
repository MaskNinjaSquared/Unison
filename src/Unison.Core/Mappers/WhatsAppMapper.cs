using System;
using System.Globalization;
using Unison.Core.Models;

namespace Unison.Core.Mappers
{
    /// <summary>
    /// Shared formatting helpers for chat-list previews and timestamps.
    /// Domain→UI mapping that depends on protocol types stays in Uwp/Baileys adapters.
    /// </summary>
    public static class WhatsAppMapper
    {
        private const int PreviewMaxLength = 50;

        /// <summary>
        /// Clock used after GMT 0 → device local. Settings writes this; converters read it.
        /// </summary>
        public static TimeFormat CurrentTimeFormat { get; set; } = TimeFormat.Hours24;

        /// <summary>
        /// Instant in the device time zone. Message stamps are GMT 0 (UTC);
        /// <see cref="DateTimeKind.Unspecified"/> (SQLite) is treated as UTC.
        /// Leftover <see cref="DateTimeKind.Local"/> values are converted to UTC first.
        /// </summary>
        public static DateTime ToDeviceLocal(DateTime timestamp)
        {
            if (timestamp == DateTime.MinValue)
            {
                return timestamp;
            }

            DateTime utc = ToUtc(timestamp);
            return TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local);
        }

        /// <summary>Normalizes a chat stamp to UTC for storage and comparison.</summary>
        public static DateTime ToUtc(DateTime timestamp)
        {
            if (timestamp == DateTime.MinValue)
            {
                return timestamp;
            }

            if (timestamp.Kind == DateTimeKind.Utc)
            {
                return timestamp;
            }

            if (timestamp.Kind == DateTimeKind.Local)
            {
                return timestamp.ToUniversalTime();
            }

            return DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);
        }

        public static string FormatLocalTime(DateTime timestamp)
        {
            return timestamp == DateTime.MinValue
                ? string.Empty
                : FormatClock(ToDeviceLocal(timestamp));
        }

        public static string FormatLocalDate(DateTime timestamp)
        {
            return timestamp == DateTime.MinValue
                ? string.Empty
                : ToDeviceLocal(timestamp).ToString("d");
        }

        /// <summary>
        /// Calendar day in the device time zone, or <see cref="DateTime.MinValue"/> when unset.
        /// </summary>
        public static DateTime ToLocalCalendarDate(DateTime timestamp)
        {
            return timestamp == DateTime.MinValue
                ? DateTime.MinValue
                : ToDeviceLocal(timestamp).Date;
        }

        /// <summary>
        /// Timeline date chip: today / yesterday / culture short date (<c>d</c>).
        /// </summary>
        public static string FormatDaySeparator(DateTime timestamp, string todayLabel, string yesterdayLabel)
        {
            if (timestamp == DateTime.MinValue)
            {
                return string.Empty;
            }

            DateTime local = ToDeviceLocal(timestamp);
            DateTime date = local.Date;
            DateTime today = DateTime.Today;

            if (date == today)
            {
                return string.IsNullOrWhiteSpace(todayLabel) ? "Today" : todayLabel.Trim();
            }

            if (date == today.AddDays(-1))
            {
                return string.IsNullOrWhiteSpace(yesterdayLabel) ? "Yesterday" : yesterdayLabel.Trim();
            }

            return local.ToString("d");
        }

        public static string FormatLocalDateTime(DateTime timestamp)
        {
            if (timestamp == DateTime.MinValue)
            {
                return string.Empty;
            }

            DateTime local = ToDeviceLocal(timestamp);
            return local.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) + " " + FormatClock(local);
        }

        /// <summary>
        /// Chat-list style relative timestamp: today → HH:mm, yesterday → localized label,
        /// within a week → weekday name, older → dd/MM/yyyy.
        /// UTC values are converted to local before comparison.
        /// </summary>
        public static string FormatTimestamp(DateTime? timestamp, string yesterdayLabel)
        {
            if (!timestamp.HasValue || timestamp.Value == DateTime.MinValue)
            {
                return string.Empty;
            }

            DateTime local = ToDeviceLocal(timestamp.Value);
            DateTime date = local.Date;
            DateTime today = DateTime.Today;

            if (date == today)
            {
                return FormatClock(local);
            }

            if (date == today.AddDays(-1))
            {
                return string.IsNullOrWhiteSpace(yesterdayLabel) ? "Yesterday" : yesterdayLabel.Trim();
            }

            int daysAgo = (today - date).Days;
            if (daysAgo <= 7)
            {
                return local.ToString("dddd");
            }

            return local.ToString("dd/MM/yyyy");
        }

        /// <summary>24h stays HH:mm; 12h is h:mm AM/PM after the local-zone conversion.</summary>
        private static string FormatClock(DateTime local)
        {
            return CurrentTimeFormat == TimeFormat.Hours12
                ? local.ToString("h:mm tt", CultureInfo.InvariantCulture)
                : local.ToString("HH:mm");
        }

        public static string FormatPreview(string body)
        {
            if (string.IsNullOrEmpty(body)) return string.Empty;

            string flat = body.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
            return flat.Length > PreviewMaxLength
                ? flat.Substring(0, PreviewMaxLength) + "..."
                : flat;
        }
    }
}
