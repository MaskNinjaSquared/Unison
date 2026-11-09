// =============================================================================
// EmptyChatOrderTimestamp
//
// When a chat has no listable tip, SortChatsForDisplay would bury it under
// DateTime.MinValue. Prefer an activity timestamp from history / wire creation,
// then an explicit fallback (join/create clock). Never invent a tip body — only
// the order key (LastMessageTimestampUtc).
// =============================================================================
using System;

namespace Unison.Core.Helpers.Chats
{
    public static class EmptyChatOrderTimestamp
    {
        /// <summary>
        /// Resolves a synthetic list-order instant. <paramref name="existingTipUtc"/> wins
        /// when already set. Otherwise: conversation timestamp, created-at, group creation,
        /// then <paramref name="fallbackUtc"/>.
        /// </summary>
        public static DateTime? Resolve(
            DateTime? existingTipUtc,
            long conversationTimestampUnix = 0,
            bool hasConversationTimestamp = false,
            long createdAtUnix = 0,
            bool hasCreatedAt = false,
            long groupCreationUnixSeconds = 0,
            DateTime? fallbackUtc = null)
        {
            if (existingTipUtc.HasValue)
            {
                return existingTipUtc;
            }

            if (hasConversationTimestamp)
            {
                DateTime? fromConversation = FromUnixSeconds(conversationTimestampUnix);
                if (fromConversation.HasValue)
                {
                    return fromConversation;
                }
            }

            if (hasCreatedAt)
            {
                DateTime? fromCreated = FromUnixSeconds(createdAtUnix);
                if (fromCreated.HasValue)
                {
                    return fromCreated;
                }
            }

            DateTime? fromCreation = FromUnixSeconds(groupCreationUnixSeconds);
            if (fromCreation.HasValue)
            {
                return fromCreation;
            }

            return fallbackUtc;
        }

        /// <summary>Unix seconds → UTC. Null when missing / non-positive / out of range.</summary>
        public static DateTime? FromUnixSeconds(long seconds)
        {
            if (seconds <= 0)
            {
                return null;
            }

            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        /// <summary>Proto ulong fields; 0 and overflow are ignored.</summary>
        public static DateTime? FromUnixSeconds(ulong seconds)
        {
            if (seconds == 0 || seconds > (ulong)long.MaxValue)
            {
                return null;
            }

            return FromUnixSeconds((long)seconds);
        }
    }
}
