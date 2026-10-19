// =============================================================================
// LegacyDeletedPreview
//
// Builds before v5.14 could store a replayed revoke as a new "[Message Deleted]"
// row stamped with the current time, and the list line then showed it as the
// newest message. These answer which rows carry that mark and which stored
// message should take the line back.
//
// A revoke is now the IsRevoked flag with an empty body, so nothing writes the
// marker any more: a row repaired once is never suspect again. Old strips
// cannot tell that fake row from a real revoke, so a real one is replaced too.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class LegacyDeletedPreview
    {
        public const string Marker = "[Message Deleted]";

        public static bool IsSuspect(ChatItem chat) =>
            chat != null &&
            !string.IsNullOrWhiteSpace(chat.JID) &&
            string.Equals(chat.LastMessage, Marker, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The newest message that is neither the marker nor a revoke and has a believable
        /// clock. <paramref name="oldestFirst"/> is read from the end.
        /// </summary>
        public static ChatMessage PickReplacement(IReadOnlyList<ChatMessage> oldestFirst, DateTime utcNow)
        {
            if (oldestFirst == null)
            {
                return null;
            }

            for (int i = oldestFirst.Count - 1; i >= 0; i--)
            {
                ChatMessage candidate = oldestFirst[i];
                if (candidate != null &&
                    MessageTimestampValidity.IsValid(candidate.Timestamp, utcNow) &&
                    !string.Equals(candidate.Content, Marker, StringComparison.OrdinalIgnoreCase) &&
                    !candidate.IsRevoked)
                {
                    return candidate;
                }
            }

            return null;
        }
    }
}
