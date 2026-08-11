using System;

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

            DateTime value = timestamp.Value;
            DateTime local = value.Kind == DateTimeKind.Utc
                ? value.ToLocalTime()
                : value;

            DateTime date = local.Date;
            DateTime today = DateTime.Today;

            if (date == today)
            {
                return local.ToString("HH:mm");
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
