// =============================================================================
// DuplicateChatRowMerge
//
// The dedupe pass collapses every set of chat rows that resolve to one canonical
// address. Two questions: which row survives, and what each of the others hands
// over before it is removed.
//
// Sibling to TransientChatMerge, which answers the second question for a single
// LID row folding into a known contact. This pass is broader -- any duplicates,
// at startup and after a sync -- and keeps its own narrower rule: it does not
// touch names, and a picture counts only by its preview url.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class DuplicateChatRowMerge
    {
        /// <summary>
        /// The rows in survival order; the first one is kept.
        /// </summary>
        /// <remarks>
        /// A phone-addressed row beats a LID row, then a row with a picture, then the row
        /// with more messages cached. Ties keep list order.
        /// </remarks>
        public static List<ChatItem> OrderForMerge(
            IEnumerable<ChatItem> rows,
            Func<ChatItem, bool> isLidAddressed,
            Func<ChatItem, int> cachedMessageCount)
        {
            if (rows == null)
            {
                return new List<ChatItem>();
            }

            return rows
                .Where(row => row != null)
                .OrderByDescending(row => !isLidAddressed(row))
                .ThenByDescending(row => !string.IsNullOrWhiteSpace(row.AvatarUrl))
                .ThenByDescending(cachedMessageCount)
                .ToList();
        }

        /// <summary>
        /// Folds what <paramref name="secondary"/> knows into <paramref name="primary"/>.
        /// The caller removes the secondary row afterwards.
        /// </summary>
        /// <param name="primaryNewestMessageUtc">
        /// Dates a row whose preview carries no stamp; <see cref="DateTime.MinValue"/> when
        /// nothing is cached.
        /// </param>
        public static void Absorb(
            ChatItem primary,
            ChatItem secondary,
            DateTime primaryNewestMessageUtc,
            DateTime secondaryNewestMessageUtc)
        {
            if (primary == null || secondary == null || ReferenceEquals(primary, secondary))
            {
                return;
            }

            AbsorbPreview(primary, secondary, primaryNewestMessageUtc, secondaryNewestMessageUtc);

            // Both rows were counting the same conversation; adding them would double-count.
            if (primary.UnreadCount < secondary.UnreadCount)
            {
                primary.UnreadCount = secondary.UnreadCount;
            }

            AbsorbAvatar(primary, secondary);
            AbsorbPin(primary, secondary);
        }

        /// <remarks>
        /// The fields move together. Half a preview -- new text over the old author line, or
        /// over the id of the message it replaced -- renders a message that was never sent,
        /// and <see cref="ChatPreviewTip"/> only fills a missing id, never a wrong one.
        /// </remarks>
        private static void AbsorbPreview(
            ChatItem primary,
            ChatItem secondary,
            DateTime primaryNewestMessageUtc,
            DateTime secondaryNewestMessageUtc)
        {
            if (string.IsNullOrWhiteSpace(secondary.LastMessage))
            {
                return;
            }

            DateTime primaryAt = PreviewAt(primary, primaryNewestMessageUtc);
            DateTime secondaryAt = PreviewAt(secondary, secondaryNewestMessageUtc);
            if (secondaryAt <= primaryAt && !string.IsNullOrWhiteSpace(primary.LastMessage))
            {
                return;
            }

            primary.LastMessage = secondary.LastMessage;
            primary.LastMessageKind = secondary.LastMessageKind;
            primary.LastPreview.CopyFrom(secondary.LastPreview);
            primary.Timestamp = secondary.Timestamp;
            primary.LastMessageTimestampUtc = secondary.LastMessageTimestampUtc;
            primary.LastMessageId = secondary.LastMessageId;
        }

        private static DateTime PreviewAt(ChatItem row, DateTime newestMessageUtc)
        {
            return row.LastMessageTimestampUtc.HasValue
                ? ChatMessageOrder.ToComparableUtc(row.LastMessageTimestampUtc.Value)
                : newestMessageUtc;
        }

        /// <remarks>
        /// The failure stamps travel with the url so a copied-in picture does not inherit a
        /// retry history that belongs to a different fetch.
        /// </remarks>
        private static void AbsorbAvatar(ChatItem primary, ChatItem secondary)
        {
            if (!string.IsNullOrWhiteSpace(primary.AvatarUrl) || string.IsNullOrWhiteSpace(secondary.AvatarUrl))
            {
                return;
            }

            primary.AvatarUrl = secondary.AvatarUrl;
            primary.AvatarFetchedAtUtc = secondary.AvatarFetchedAtUtc;
            primary.AvatarFetchFailedAtUtc = secondary.AvatarFetchFailedAtUtc;
            primary.AvatarFetchFailureReason = secondary.AvatarFetchFailureReason;
        }

        /// <remarks>
        /// history_chat_preview has no pin columns, so a sync-created primary is unpinned and
        /// would sink the conversation. A stamp of 0 is an unpin and does not travel.
        /// </remarks>
        private static void AbsorbPin(ChatItem primary, ChatItem secondary)
        {
            if (!secondary.IsChatPinned || secondary.PinnedTimestamp == 0)
            {
                return;
            }

            if (primary.IsChatPinned && (secondary.PinnedTimestamp ?? 0) <= (primary.PinnedTimestamp ?? 0))
            {
                return;
            }

            primary.IsChatPinned = true;
            primary.PinnedTimestamp = secondary.PinnedTimestamp;
        }
    }
}
