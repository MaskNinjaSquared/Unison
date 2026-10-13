// =============================================================================
// ActiveChatMessageWindow
//
// Only the open conversation keeps a large slice of its timeline in memory; the
// full history stays in SQLite. When the slice grows past its bound, something
// has to leave, and what leaves vanishes from the screen until the user scrolls
// back for it.
//
// Live pins never leave: the pinned banner reads from this list, so trimming a
// pin away would drop it off the top of the chat while it is still pinned. The
// rest of the room goes to the newest messages.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class ActiveChatMessageWindow
    {
        /// <summary>
        /// Cuts <paramref name="messages"/> down to <paramref name="capacity"/> in place,
        /// keeping every live pin plus the newest of the rest, in timeline order.
        /// </summary>
        /// <returns>Whether the list was over capacity and has been rewritten.</returns>
        /// <remarks>
        /// Pins count against the capacity but are never dropped for it, so more live pins
        /// than room leaves the window holding only pins, over its bound.
        ///
        /// Newest is decided by <see cref="ChatMessageOrder"/>, not the raw clock: the list
        /// mixes live messages with rows read back from SQLite as
        /// <see cref="DateTimeKind.Unspecified"/>, and a raw comparison is off by the local
        /// offset.
        /// </remarks>
        public static bool TrimInPlace(List<ChatMessage> messages, int capacity, DateTime nowUtc)
        {
            if (messages == null || messages.Count <= capacity)
            {
                return false;
            }

            var pinned = new List<ChatMessage>();
            var regular = new List<ChatMessage>();
            foreach (ChatMessage message in messages)
            {
                if (message == null)
                {
                    continue;
                }

                (IsLivePin(message, nowUtc) ? pinned : regular).Add(message);
            }

            var kept = new List<ChatMessage>(Math.Max(capacity, pinned.Count));
            var keptIds = new HashSet<string>(StringComparer.Ordinal);
            ChatMessageOrder.SortInPlace(pinned);
            foreach (ChatMessage pin in pinned)
            {
                Keep(kept, keptIds, pin);
            }

            int room = Math.Max(0, capacity - kept.Count);
            ChatMessageOrder.SortInPlace(regular);
            var newestFirst = new List<ChatMessage>(room);
            for (int i = regular.Count - 1; i >= 0 && newestFirst.Count < room; i--)
            {
                Keep(newestFirst, keptIds, regular[i], alsoCheck: kept);
            }

            kept.AddRange(newestFirst);
            ChatMessageOrder.SortInPlace(kept);

            messages.Clear();
            messages.AddRange(kept);
            return true;
        }

        private static bool IsLivePin(ChatMessage message, DateTime nowUtc)
        {
            return message.IsPinned &&
                   (!message.PinExpiresAtUtc.HasValue || message.PinExpiresAtUtc.Value > nowUtc);
        }

        /// <summary>
        /// Adds a message unless it is already kept: by id when it has one, by reference when
        /// it does not, since two id-less messages are not the same message.
        /// </summary>
        private static bool Keep(
            List<ChatMessage> into,
            HashSet<string> keptIds,
            ChatMessage message,
            List<ChatMessage> alsoCheck = null)
        {
            if (string.IsNullOrWhiteSpace(message.Id))
            {
                if (into.Contains(message) || (alsoCheck != null && alsoCheck.Contains(message)))
                {
                    return false;
                }
            }
            else if (!keptIds.Add(message.Id))
            {
                return false;
            }

            into.Add(message);
            return true;
        }
    }
}
