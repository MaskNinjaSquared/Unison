// =============================================================================
// AppStateChatMutation
//
// What a mutation arriving from the account does to a chat row: the unread
// count when the phone marks a conversation read or unread, and the archive /
// pin / mute flags.
//
// These arrive for one address but land on every row that shares a canonical
// identity, because a conversation can be listed under both its PN and its LID
// form. That is why the unread rule reads across rows instead of just clearing
// one, and why an unpin is written as an explicit zero rather than as absence.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// The flags one app-state mutation carries. Each is absent when the mutation did
    /// not speak about it, which is not the same as it having been turned off.
    /// </summary>
    public struct ChatFlagChange
    {
        public bool? Archived;
        public bool? Pinned;
        public long? PinnedTimestamp;

        /// <summary>
        /// Only read when <see cref="AppliesMute"/> is set, because null here is a
        /// meaningful value: it is what an unmuted chat looks like.
        /// </summary>
        public long? MuteEndTimestamp;
        public bool AppliesMute;

        public bool TouchesAnything => Archived.HasValue || Pinned.HasValue || AppliesMute;
    }

    public static class AppStateChatMutation
    {
        /// <summary>
        /// The unread count every row of a conversation should show after the account
        /// marked it read or unread.
        /// </summary>
        /// <remarks>
        /// Marking unread has no number attached, so the highest count among the rows is
        /// kept — one of them may still know how many messages were actually waiting.
        /// Failing that it is at least one, otherwise "mark as unread" would leave the
        /// conversation looking read.
        /// </remarks>
        public static int ResolveUnreadCount(IReadOnlyList<ChatItem> rows, bool read)
        {
            if (read)
            {
                return 0;
            }

            return Math.Max(1, ChatUnreadTally.HighestAmong(rows));
        }

        /// <summary>
        /// Writes the flags the mutation carries onto one row, leaving the ones it did
        /// not mention alone.
        /// </summary>
        /// <param name="nowUnixMs">
        /// Used only when a pin arrives without a timestamp of its own and the row has
        /// none either.
        /// </param>
        public static void ApplyFlags(ChatItem chat, ChatFlagChange change, long nowUnixMs)
        {
            if (chat == null)
            {
                return;
            }

            if (change.Archived.HasValue)
            {
                chat.IsArchived = change.Archived.Value;
            }

            if (change.Pinned.HasValue)
            {
                chat.IsChatPinned = change.Pinned.Value;
                // 0 marks an explicit unpin so PN/LID dedupe cannot resurrect the pin
                // from an alias row that has not received the same mutation yet.
                chat.PinnedTimestamp = change.Pinned.Value
                    ? (long?)(change.PinnedTimestamp ?? chat.PinnedTimestamp ?? nowUnixMs)
                    : 0;
            }

            if (change.AppliesMute)
            {
                // null = unmuted; WhatsApp forever may arrive as 0.
                chat.MutedUntil = change.MuteEndTimestamp;
            }
        }
    }
}
