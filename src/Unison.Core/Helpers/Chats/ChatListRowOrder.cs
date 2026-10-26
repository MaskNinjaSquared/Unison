// =============================================================================
// ChatListRowOrder
//
// Same pin / recency / name rules the chat list shows. TargetIndex walks only
// the visible rows (Unigram NextIndexOf) so a tip does not re-sort the catalogue.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers.Chats
{
    public static class ChatListRowOrder
    {
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
        /// Where <paramref name="chat"/> belongs among <paramref name="visible"/> after its
        /// tip/pin changed. Does not allocate a sorted copy.
        /// </summary>
        public static int TargetIndex(IList<ChatItem> visible, ChatItem chat)
        {
            return TargetIndex(visible, chat, orderProbe: chat);
        }

        /// <summary>
        /// Same as <see cref="TargetIndex(IList{ChatItem}, ChatItem)"/> but orders by
        /// <paramref name="orderProbe"/> (fresher catalogue tip) while skipping the on-screen
        /// <paramref name="rowToMove"/> slot — PN/LID alias pairs.
        /// </summary>
        public static int TargetIndex(
            IList<ChatItem> visible,
            ChatItem rowToMove,
            ChatItem orderProbe)
        {
            if (visible == null || rowToMove == null || visible.Count == 0)
            {
                return 0;
            }

            if (orderProbe == null)
            {
                orderProbe = rowToMove;
            }

            int targetIndex = 0;
            for (int i = 0; i < visible.Count; i++)
            {
                ChatItem other = visible[i];
                if (other == null ||
                    ReferenceEquals(other, rowToMove) ||
                    ReferenceEquals(other, orderProbe))
                {
                    continue;
                }

                if (Compare(other, orderProbe) < 0)
                {
                    targetIndex++;
                }
            }

            return targetIndex;
        }
    }
}
