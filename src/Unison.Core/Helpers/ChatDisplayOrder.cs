// =============================================================================
// ChatDisplayOrder
//
// The order rows appear in on the chat list, and the cheapest way to restore it
// after something changes.
//
// Sibling of ChatMessageOrder, which does the same job one level down. Both were
// private to WhatsAppService; ordering is a rule about ChatItem, not about the
// session, and the store that owns the collection lives here.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class ChatDisplayOrder
    {
        /// <summary>
        /// Pinned first and by pin time, then by last message, then by name. Name is the
        /// tie-break rather than a coin toss so that a list of chats with no messages yet — a
        /// fresh sync — does not reshuffle itself between passes.
        /// </summary>
        public static int Compare(ChatItem left, ChatItem right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left == null)
            {
                return 1;
            }
            if (right == null)
            {
                return -1;
            }

            if (left.IsChatPinned != right.IsChatPinned)
            {
                return left.IsChatPinned ? -1 : 1;
            }

            if (left.IsChatPinned)
            {
                long leftPin = left.PinnedTimestamp ?? 0;
                long rightPin = right.PinnedTimestamp ?? 0;
                int pinCompare = rightPin.CompareTo(leftPin);
                if (pinCompare != 0)
                {
                    return pinCompare;
                }
            }

            DateTime leftTime = left.LastMessageTimestampUtc.HasValue
                ? ChatMessageOrder.ToComparableUtc(left.LastMessageTimestampUtc.Value)
                : DateTime.MinValue;
            DateTime rightTime = right.LastMessageTimestampUtc.HasValue
                ? ChatMessageOrder.ToComparableUtc(right.LastMessageTimestampUtc.Value)
                : DateTime.MinValue;

            int timeCompare = rightTime.CompareTo(leftTime);
            if (timeCompare != 0)
            {
                return timeCompare;
            }

            return string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase);
        }

        /// <summary>
        /// Moves one row to where it now belongs. Cheaper than a full sort when a single chat
        /// changed, which is the common case: a message arrived in one conversation.
        /// </summary>
        public static void Reposition(ObservableCollection<ChatItem> chats, ChatItem chat)
        {
            if (chats == null || chat == null || !chats.Contains(chat))
            {
                return;
            }

            int targetIndex = 0;
            foreach (var other in chats)
            {
                if (ReferenceEquals(other, chat))
                {
                    continue;
                }

                if (Compare(other, chat) < 0)
                {
                    targetIndex++;
                }
            }

            int currentIndex = chats.IndexOf(chat);
            if (currentIndex >= 0 && currentIndex != targetIndex)
            {
                chats.Move(currentIndex, targetIndex);
            }
        }

        public static void SortInPlace(ObservableCollection<ChatItem> chats)
        {
            if (chats == null || chats.Count < 2)
            {
                return;
            }

            var desired = chats.OrderBy(c => c, Comparer<ChatItem>.Create(Compare)).ToList();

            // The list is usually already in order - a preview that did not change position, a
            // name that was filled in - and every Move below is a collection-changed notification
            // the ListView has to act on. Finding that out costs one pass.
            int firstOutOfPlace = -1;
            for (int i = 0; i < desired.Count; i++)
            {
                if (!ReferenceEquals(chats[i], desired[i]))
                {
                    firstOutOfPlace = i;
                    break;
                }
            }

            if (firstOutOfPlace < 0)
            {
                return;
            }

            for (int i = firstOutOfPlace; i < desired.Count; i++)
            {
                if (ReferenceEquals(chats[i], desired[i]))
                {
                    continue;
                }

                // Everything before i is already in its final place, so the search starts there.
                for (int j = i + 1; j < chats.Count; j++)
                {
                    if (ReferenceEquals(chats[j], desired[i]))
                    {
                        chats.Move(j, i);
                        break;
                    }
                }
            }
        }
    }
}
