// =============================================================================
// SelfDisplayNameResolution
//
// The account's own base name (no "(You)" marker; the UI adds that). Sources are
// asked in the owner's order and read only when the ones before them had
// nothing usable; the phone number stands in when none of them does.
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.Core.Helpers
{
    public static class SelfDisplayNameResolution
    {
        public const string PhonePlaceholderSource = "me-phone-placeholder";

        public struct Result
        {
            public string Name;

            /// <summary>Which source answered; null when nothing did.</summary>
            public string Source;
        }

        /// <param name="sources">Source label and a reader, in precedence order.</param>
        /// <param name="normalize">Returns the usable name in a candidate, or blank.</param>
        public static Result Pick(
            IEnumerable<KeyValuePair<string, Func<string>>> sources,
            Func<string, string> normalize,
            string phoneFallback)
        {
            if (sources != null && normalize != null)
            {
                foreach (KeyValuePair<string, Func<string>> source in sources)
                {
                    string name = normalize(source.Value?.Invoke());
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        return new Result { Name = name.Trim(), Source = source.Key };
                    }
                }
            }

            return string.IsNullOrWhiteSpace(phoneFallback)
                ? new Result { Name = string.Empty, Source = null }
                : new Result { Name = phoneFallback, Source = PhonePlaceholderSource };
        }

        /// <summary>
        /// The self name already trusted, or null while the sidebar still falls back to the
        /// number. A phone echo in the credentials is not a name.
        /// </summary>
        public static string Established(
            string currentUserName,
            string credentialsName,
            Func<string, bool> isOwnPhoneEcho)
        {
            if (!string.IsNullOrWhiteSpace(currentUserName))
            {
                return currentUserName;
            }

            return !string.IsNullOrWhiteSpace(credentialsName) &&
                   !(isOwnPhoneEcho?.Invoke(credentialsName) ?? false)
                ? credentialsName
                : null;
        }
    }
}
