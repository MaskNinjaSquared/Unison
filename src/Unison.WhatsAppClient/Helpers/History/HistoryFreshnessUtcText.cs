// =============================================================================
// HistoryFreshnessUtcText
//
// Parses round-trip ISO timestamps stored in local settings for history
// freshness repair bookkeeping.
// =============================================================================
using System;
using System.Globalization;

namespace Unison.WhatsAppClient.Helpers
{
    public static class HistoryFreshnessUtcText
    {
        public static bool TryParseRoundtrip(string rawText, out DateTime timestampUtc)
        {
            timestampUtc = default;
            if (string.IsNullOrEmpty(rawText))
            {
                return false;
            }

            if (!DateTime.TryParse(
                    rawText,
                    null,
                    DateTimeStyles.RoundtripKind,
                    out DateTime parsed))
            {
                return false;
            }

            timestampUtc = parsed.Kind == DateTimeKind.Utc
                ? parsed
                : parsed.ToUniversalTime();
            return true;
        }
    }
}
