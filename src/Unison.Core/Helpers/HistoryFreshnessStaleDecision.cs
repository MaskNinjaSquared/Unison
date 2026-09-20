// =============================================================================
// HistoryFreshnessStaleDecision
//
// Whether stored tips still look "owed" enough to keep FULL_HISTORY catch-up
// going. The client gathers newest-any / newest-non-self / newest-group watermarks;
// this answers from those timestamps alone. Reason strings stay diagnostic.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public static class HistoryFreshnessStaleDecision
    {
        /// <summary>Default age beyond which a tip scope is treated as stale (30 minutes).</summary>
        public static readonly TimeSpan DefaultStaleThreshold = TimeSpan.FromMinutes(30);

        /// <summary>
        /// True when history still looks behind. <paramref name="reason"/> is set only then.
        /// </summary>
        /// <param name="nowUtc">Clock for age comparisons.</param>
        /// <param name="newestAnyUtc">Newest tip across all chats (MinValue = none).</param>
        /// <param name="newestNonSelfUtc">Newest tip excluding self chats.</param>
        /// <param name="hasGroupChats">Whether the catalogue has any group rows.</param>
        /// <param name="newestGroupUtc">Newest tip among groups (ignored when no groups).</param>
        /// <param name="staleThreshold">Max age before a scope is stale.</param>
        /// <param name="reason">Diagnostic token when stale; null when fresh.</param>
        public static bool IsStale(
            DateTime nowUtc,
            DateTime newestAnyUtc,
            DateTime newestNonSelfUtc,
            bool hasGroupChats,
            DateTime newestGroupUtc,
            TimeSpan staleThreshold,
            out string reason)
        {
            if (newestAnyUtc == DateTime.MinValue)
            {
                reason = "no-stored-messages";
                return true;
            }

            if (newestNonSelfUtc == DateTime.MinValue)
            {
                reason = "no-non-self-messages:newestAny=" + Format(newestAnyUtc);
                return true;
            }

            TimeSpan newestNonSelfAge = nowUtc - newestNonSelfUtc;
            if (newestNonSelfAge > staleThreshold)
            {
                reason = "non-self-stale:" + newestNonSelfUtc.ToString("O") +
                         ":ageMinutes=" + newestNonSelfAge.TotalMinutes.ToString("F1") +
                         ":newestAny=" + Format(newestAnyUtc);
                return true;
            }

            if (hasGroupChats)
            {
                if (newestGroupUtc == DateTime.MinValue)
                {
                    reason = "no-group-messages:newestAny=" + Format(newestAnyUtc) +
                             ":newestNonSelf=" + Format(newestNonSelfUtc);
                    return true;
                }

                TimeSpan newestGroupAge = nowUtc - newestGroupUtc;
                if (newestGroupAge > staleThreshold)
                {
                    reason = "group-stale:" + newestGroupUtc.ToString("O") +
                             ":ageMinutes=" + newestGroupAge.TotalMinutes.ToString("F1") +
                             ":newestAny=" + Format(newestAnyUtc) +
                             ":newestNonSelf=" + Format(newestNonSelfUtc);
                    return true;
                }
            }

            TimeSpan newestAnyAge = nowUtc - newestAnyUtc;
            if (newestAnyAge > staleThreshold)
            {
                reason = "newest-stale:" + newestAnyUtc.ToString("O") +
                         ":ageMinutes=" + newestAnyAge.TotalMinutes.ToString("F1");
                return true;
            }

            reason = null;
            return false;
        }

        private static string Format(DateTime timestamp)
        {
            return timestamp == DateTime.MinValue ? "<none>" : timestamp.ToString("O");
        }
    }
}
