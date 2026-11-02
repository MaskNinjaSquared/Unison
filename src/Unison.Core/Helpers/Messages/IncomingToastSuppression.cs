// =============================================================================
// IncomingToastSuppression
//
// Whether an inbound envelope must stay quiet (no toast) during sync waves or
// when the message is older than a freshness window (history bleed).
// =============================================================================
using System;

namespace Unison.Core.Helpers.Messages
{
    /// <summary>
    /// Pure gate for toast suppression on live/offline inbound traffic.
    /// Unread / list strip still update; only the popup is gated.
    /// </summary>
    public static class IncomingToastSuppression
    {
        /// <summary>
        /// Messages older than this do not toast (covers history catch-up bleed
        /// labeled as live). Default five minutes.
        /// </summary>
        public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromMinutes(5);

        /// <summary>Allow clocks slightly ahead of the device without treating as stale.</summary>
        public static readonly TimeSpan FutureSkewGrace = TimeSpan.FromMinutes(2);

        /// <summary>
        /// True when the toast must not fire: initial sync, history catch-up /
        /// silent FULL wave, offline replay envelopes, or stale timestamp.
        /// </summary>
        public static bool ShouldQuiet(
            bool initialSyncActive,
            bool awaitingInitialSync,
            bool historyCatchUpQuiet,
            bool isOfflineEnvelope,
            DateTime messageTimestamp,
            DateTime nowUtc,
            TimeSpan? maxAge = null)
        {
            if (initialSyncActive || awaitingInitialSync || historyCatchUpQuiet || isOfflineEnvelope)
            {
                return true;
            }

            if (messageTimestamp == default(DateTime))
            {
                return false;
            }

            DateTime tsUtc = ToUtc(messageTimestamp);
            DateTime now = ToUtc(nowUtc);
            TimeSpan age = now - tsUtc;

            // Far in the future: ignore age gate (treat as fresh).
            if (age < TimeSpan.Zero && age.Duration() <= FutureSkewGrace)
            {
                return false;
            }

            if (age < TimeSpan.Zero)
            {
                return false;
            }

            TimeSpan window = maxAge ?? DefaultMaxAge;
            return age > window;
        }

        private static DateTime ToUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc)
            {
                return value;
            }

            if (value.Kind == DateTimeKind.Local)
            {
                return value.ToUniversalTime();
            }

            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }
    }
}
