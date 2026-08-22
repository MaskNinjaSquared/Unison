using System;
using System.Globalization;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// The wire format for startup phases reported through <c>IWhatsAppService.OnSyncStatus</c>.
    /// </summary>
    /// <remarks>
    /// The service used to push finished English sentences ("Fetching contact names..."), which the
    /// chat list then showed verbatim - so those phases were the only part of the UI that never
    /// translated. A phase now travels as a symbolic token plus its counters, and the view model is
    /// the only place that turns it into words, from <c>Strings/{tag}/Resources.resw</c>.
    /// <para>
    /// Anything that is not a token still passes through untouched: the older literal messages and
    /// the resync wording keep working while they are migrated.
    /// </para>
    /// </remarks>
    public static class SyncPhaseStatus
    {
        private const string Prefix = "phase:";

        /// <summary>Deliberate wait after replay, before names and pictures are asked for.</summary>
        public const string Settling = "settling";

        /// <summary>Resolving display names for chats still showing a bare number.</summary>
        public const string Names = "names";

        /// <summary>Fetching profile pictures.</summary>
        public const string Avatars = "avatars";

        /// <summary>Fetching group listings and subjects.</summary>
        public const string Groups = "groups";

        /// <summary>Enrichment gave up this round because the device is under memory pressure.</summary>
        public const string LowMemory = "lowmemory";

        /// <summary>A phase with no meaningful count (<c>phase:settling</c>).</summary>
        public static string Format(string phase)
        {
            return string.IsNullOrWhiteSpace(phase) ? null : Prefix + phase;
        }

        /// <summary>
        /// A phase carrying progress (<c>phase:names:12/40</c>). A total of zero degrades to the
        /// countless form rather than rendering "12 of 0".
        /// </summary>
        public static string Format(string phase, int current, int total)
        {
            if (string.IsNullOrWhiteSpace(phase))
            {
                return null;
            }

            if (total <= 0)
            {
                return Prefix + phase;
            }

            return Prefix + phase + ":" +
                   current.ToString(CultureInfo.InvariantCulture) + "/" +
                   total.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Splits a token back into its phase and counters. False for anything that is not a
        /// token, which the caller should then treat as literal text.
        /// </summary>
        public static bool TryParse(string message, out string phase, out int current, out int total)
        {
            phase = null;
            current = 0;
            total = 0;

            if (string.IsNullOrWhiteSpace(message) ||
                !message.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return false;
            }

            string body = message.Substring(Prefix.Length);
            if (body.Length == 0)
            {
                return false;
            }

            int separator = body.IndexOf(':');
            if (separator < 0)
            {
                phase = body;
                return true;
            }

            phase = body.Substring(0, separator);
            if (phase.Length == 0)
            {
                return false;
            }

            string counters = body.Substring(separator + 1);
            int slash = counters.IndexOf('/');
            if (slash < 0)
            {
                return true;
            }

            int.TryParse(
                counters.Substring(0, slash),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out current);
            int.TryParse(
                counters.Substring(slash + 1),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out total);

            return true;
        }
    }
}
