using System;
using System.Collections.Generic;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Subjects that look like invite-link placeholders rather than a real group name.
    /// Blacklisted synced names are ignored unless the chat has no usable name yet.
    /// </summary>
    public static class GroupNameSyncBlacklist
    {
        private static readonly string[] Tokens =
        {
            "invite"
        };

        /// <summary>Lowercase substrings matched against a candidate group subject.</summary>
        public static IReadOnlyList<string> GetTokens() => Tokens;

        /// <summary>
        /// True when <paramref name="name"/> contains any blacklist token
        /// (case-insensitive; tokens themselves are stored lowercase).
        /// </summary>
        public static bool IsBlacklisted(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            string haystack = name.Trim().ToLowerInvariant();
            for (int i = 0; i < Tokens.Length; i++)
            {
                if (haystack.IndexOf(Tokens[i], StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether a synced subject should replace the current label.
        /// Blacklisted subjects only win when there is no usable name yet.
        /// </summary>
        public static bool ShouldApplySyncedSubject(
            string incomingSubject,
            bool incomingMeaningful,
            bool existingMeaningful)
        {
            if (IsBlacklisted(incomingSubject))
            {
                return !existingMeaningful;
            }

            return incomingMeaningful || !existingMeaningful;
        }

        /// <summary>
        /// Whether a synced subject may overwrite the contact-name cache.
        /// Blacklisted subjects only land when nothing usable is already cached.
        /// </summary>
        public static bool ShouldCacheSyncedSubject(
            string incomingSubject,
            bool existingCacheMeaningful)
        {
            if (!IsBlacklisted(incomingSubject))
            {
                return true;
            }

            return !existingCacheMeaningful;
        }
    }
}
