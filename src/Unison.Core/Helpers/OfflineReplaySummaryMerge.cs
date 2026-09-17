// =============================================================================
// OfflineReplaySummaryMerge
//
// How an offline-replay tip folds into the compact per-chat summary the list
// applies later. Extracted from WhatsAppService so record and rollback cannot
// disagree on which fields travel with the tip (or on how unread accumulates).
// =============================================================================
using System;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class OfflineReplaySummaryMerge
    {
        /// <summary>
        /// Creates a fresh summary row for <paramref name="jid"/> with an empty tip.
        /// </summary>
        public static OfflineReplayChatSummary Create(string jid, bool isGroup)
        {
            return new OfflineReplayChatSummary
            {
                Jid = jid ?? string.Empty,
                Timestamp = DateTime.MinValue,
                IsGroup = isGroup,
                Kind = ChatPreviewKind.Text,
                Preview = string.Empty,
                AuthorPrefix = string.Empty
            };
        }

        /// <summary>
        /// Updates <paramref name="summary"/> with a candidate tip and optional unread bump.
        /// A tip is accepted when its comparable time is usable and not older than the current tip
        /// (<c>&gt;=</c>). Unread is independent of tip acceptance.
        /// </summary>
        public static void Record(
            OfflineReplayChatSummary summary,
            string preview,
            DateTime timestamp,
            bool isGroup,
            bool isFromMe,
            ChatPreviewKind kind,
            string status,
            string authorPrefix,
            bool incrementUnread,
            DateTime utcNow)
        {
            if (summary == null)
            {
                return;
            }

            DateTime comparableTimestamp = MessageTimestampValidity.IsValid(timestamp, utcNow)
                ? ChatMessageOrder.ToComparableUtc(timestamp)
                : DateTime.MinValue;

            if (comparableTimestamp != DateTime.MinValue &&
                (summary.Timestamp == DateTime.MinValue || comparableTimestamp >= summary.Timestamp))
            {
                WriteTip(
                    summary,
                    comparableTimestamp,
                    preview,
                    isGroup,
                    isFromMe,
                    kind,
                    status,
                    authorPrefix);
            }

            if (incrementUnread)
            {
                summary.UnreadDelta++;
            }
        }

        /// <summary>
        /// Puts a snapshot back after a failed UI apply. A newer tip replaces every tip field;
        /// unread deltas always add. Uses strict <c>&gt;</c> so an equal-time tip that was already
        /// merged during record is not rewritten a second time.
        /// </summary>
        public static void Reapply(OfflineReplayChatSummary current, OfflineReplayChatSummary returned)
        {
            if (current == null || returned == null)
            {
                return;
            }

            if (ChatMessageOrder.ToComparableUtc(returned.Timestamp) >
                ChatMessageOrder.ToComparableUtc(current.Timestamp))
            {
                WriteTip(
                    current,
                    ChatMessageOrder.ToComparableUtc(returned.Timestamp),
                    returned.Preview,
                    returned.IsGroup,
                    returned.IsFromMe,
                    returned.Kind,
                    returned.Status,
                    returned.AuthorPrefix);
            }

            current.UnreadDelta += returned.UnreadDelta;
        }

        private static void WriteTip(
            OfflineReplayChatSummary summary,
            DateTime comparableTimestamp,
            string preview,
            bool isGroup,
            bool isFromMe,
            ChatPreviewKind kind,
            string status,
            string authorPrefix)
        {
            summary.Timestamp = comparableTimestamp;
            summary.Preview = preview ?? string.Empty;
            summary.IsGroup = isGroup;
            summary.IsFromMe = isFromMe;
            summary.Kind = kind;
            summary.Status = status;
            summary.AuthorPrefix = authorPrefix ?? string.Empty;
        }
    }
}
