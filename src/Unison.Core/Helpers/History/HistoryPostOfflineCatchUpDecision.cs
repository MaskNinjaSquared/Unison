// =============================================================================
// HistoryPostOfflineCatchUpDecision
//
// After the offline buffer drains: whether to solicit FULL_HISTORY_SYNC_ON_DEMAND.
// Any remaining freshness stale means Meta's offline drain did not close the gap
// — always ask. A former 6h mild-age skip left companions stuck after a partial
// tip replay (empty offline queue while local tips were only mildly behind).
// =============================================================================
using System;
using System.Globalization;

namespace Unison.Core.Helpers
{
    public static class HistoryPostOfflineCatchUpDecision
    {
        /// <summary>
        /// True when the post-offline path should ask for FULL_HISTORY freshness repair.
        /// </summary>
        /// <param name="offlineCount">
        /// Kept for call-site diagnostics; request is gated only on <paramref name="stillStale"/>.
        /// </param>
        /// <param name="staleReason">
        /// Kept for call-site diagnostics / <see cref="TryGetAgeMinutes"/> callers.
        /// </param>
        /// <param name="tipAge">
        /// Optional tip age from the caller clock; unused for the gate (always ask when stale).
        /// </param>
        public static bool ShouldRequestFullHistory(
            int offlineCount,
            bool stillStale,
            string staleReason,
            TimeSpan? tipAge = null)
        {
            return stillStale;
        }

        /// <summary>
        /// Catalogue looks empty in a scope — not merely older than the age threshold.
        /// </summary>
        public static bool IsStructuralGap(string staleReason)
        {
            if (string.IsNullOrWhiteSpace(staleReason))
            {
                return false;
            }

            return staleReason.StartsWith("no-stored-messages", StringComparison.OrdinalIgnoreCase)
                || staleReason.StartsWith("no-non-self-messages", StringComparison.OrdinalIgnoreCase)
                || staleReason.StartsWith("no-group-messages", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Reads <c>ageMinutes=</c> from <see cref="HistoryFreshnessStaleDecision"/> reason strings.
        /// </summary>
        public static bool TryGetAgeMinutes(string staleReason, out double ageMinutes)
        {
            ageMinutes = 0;
            if (string.IsNullOrWhiteSpace(staleReason))
            {
                return false;
            }

            const string marker = "ageMinutes=";
            int start = staleReason.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return false;
            }

            start += marker.Length;
            int end = start;
            while (end < staleReason.Length)
            {
                char c = staleReason[end];
                if (c == ':' || c == ';' || char.IsWhiteSpace(c))
                {
                    break;
                }

                end++;
            }

            if (end <= start)
            {
                return false;
            }

            string token = staleReason.Substring(start, end - start);
            // Defense for older reason strings emitted under a comma decimal culture.
            token = token.Replace(',', '.');

            return double.TryParse(
                token,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out ageMinutes);
        }
    }
}
