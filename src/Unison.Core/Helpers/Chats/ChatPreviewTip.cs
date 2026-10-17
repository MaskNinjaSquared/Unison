// =============================================================================
// ChatPreviewTip
//
// Which message a chat row's one-line preview should show, and whether the row
// already shows it.
//
// The reconcile pass that reads these rules is mostly SQLite and diagnostics;
// the decisions themselves are three small predicates that were buried in it.
// They are the ones that matter, because getting them wrong shows the wrong
// last message in the list — a thing the user reads at a glance and would not
// think to report as a bug.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class ChatPreviewTip
    {
        /// <summary>
        /// The newer of the stored tip and the one still only in memory.
        /// </summary>
        /// <remarks>
        /// An open chat can be showing a live message SQLite has not caught up with, so
        /// neither source is authoritative on its own.
        /// </remarks>
        public static ChatMessage PickNewer(ChatMessage fromStore, ChatMessage fromMemory)
        {
            if (fromStore == null)
            {
                return fromMemory;
            }

            if (fromMemory == null)
            {
                return fromStore;
            }

            DateTime storeUtc = ChatMessageOrder.ToComparableUtc(fromStore.Timestamp);
            DateTime memoryUtc = ChatMessageOrder.ToComparableUtc(fromMemory.Timestamp);

            if (memoryUtc > storeUtc)
            {
                return fromMemory;
            }

            if (storeUtc > memoryUtc)
            {
                return fromStore;
            }

            // Same wall-clock second: prefer fromMe only as a tie-break (cross-device echo),
            // never over a strictly newer timestamp.
            if (fromMemory.IsFromMe && !fromStore.IsFromMe)
            {
                return fromMemory;
            }

            int idComparison = string.CompareOrdinal(
                fromMemory.Id ?? string.Empty,
                fromStore.Id ?? string.Empty);

            return idComparison > 0 ? fromMemory : fromStore;
        }

        /// <summary>
        /// The newest of a chat's remaining messages — the one that should become the
        /// preview after whatever was on top got deleted.
        /// </summary>
        public static ChatMessage PickLatest(IReadOnlyList<ChatMessage> messages)
        {
            if (messages == null)
            {
                return null;
            }

            ChatMessage latest = null;
            DateTime latestUtc = DateTime.MinValue;

            for (int i = 0; i < messages.Count; i++)
            {
                ChatMessage candidate = messages[i];
                if (candidate == null)
                {
                    continue;
                }

                DateTime candidateUtc = ChatMessageOrder.ToComparableUtc(candidate.Timestamp);
                if (latest == null || candidateUtc > latestUtc)
                {
                    latest = candidate;
                    latestUtc = candidateUtc;
                }
            }

            return latest;
        }

        /// <summary>
        /// Whether the reconcile pass may put this message on the strip. A revoke may, as on the
        /// phone; an unflagged "[Message Deleted]" body and an untrusted clock never may.
        /// </summary>
        /// <remarks>
        /// The unflagged marker is what builds before v5.14 left behind for a replayed revoke
        /// envelope: a fake newest message, not a deleted one.
        /// </remarks>
        public static bool IsReconcileCandidate(ChatMessage message, DateTime utcNow) =>
            message != null &&
            MessageTimestampValidity.IsValid(message.Timestamp, utcNow) &&
            (message.IsRevoked ||
             !string.Equals(message.Content, LegacyDeletedPreview.Marker, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The tip a stored page offers: its newest row that may go on the strip.
        /// </summary>
        /// <remarks>
        /// The page is oldest first. A legacy marker or a bogus clock on the top row must not hide
        /// the real tip, so the page is walked down instead of giving up on the first row.
        /// </remarks>
        public static ChatMessage PickStoredTip(IReadOnlyList<HistoryMessage> pageOldestFirst, DateTime utcNow)
        {
            if (pageOldestFirst == null)
            {
                return null;
            }

            for (int i = pageOldestFirst.Count - 1; i >= 0; i--)
            {
                HistoryMessage row = pageOldestFirst[i];
                if (row == null || !row.TimestampUtc.HasValue)
                {
                    continue;
                }

                ChatMessage mapped = HistoryMessageMapper.ToChatMessage(row);
                if (IsReconcileCandidate(mapped, utcNow))
                {
                    return mapped;
                }
            }

            return null;
        }

        /// <summary>
        /// The newest <see cref="IsReconcileCandidate"/> message; on the same clock the higher id
        /// wins, so the answer does not depend on the order the timelines were read.
        /// </summary>
        public static ChatMessage PickNewestReconcileCandidate(IEnumerable<ChatMessage> messages, DateTime utcNow)
        {
            if (messages == null)
            {
                return null;
            }

            ChatMessage best = null;
            DateTime bestUtc = DateTime.MinValue;
            foreach (ChatMessage message in messages)
            {
                if (!IsReconcileCandidate(message, utcNow))
                {
                    continue;
                }

                DateTime utc = ChatMessageOrder.ToComparableUtc(message.Timestamp);
                if (best == null ||
                    utc > bestUtc ||
                    (utc == bestUtc &&
                     string.CompareOrdinal(message.Id ?? string.Empty, best.Id ?? string.Empty) > 0))
                {
                    best = message;
                    bestUtc = utc;
                }
            }

            return best;
        }

        /// <summary>
        /// Blanks the preview of a chat with nothing left to show.
        /// </summary>
        /// <remarks>
        /// Deliberately leaves <c>LastMessageIsFromMe</c> and the delivery status alone: they
        /// are what the previous code did, and they are only read alongside the text this
        /// clears. Worth revisiting together rather than one at a time.
        /// </remarks>
        public static void Clear(ChatItem chat)
        {
            if (chat == null)
            {
                return;
            }

            chat.LastMessage = string.Empty;
            chat.LastMessageAuthor = string.Empty;
            chat.LastMessageMentionedJids = null;
            chat.LastMessageKind = ChatPreviewKind.Text;
            chat.LastMessageId = null;
            chat.Timestamp = string.Empty;
            chat.LastMessageTimestampUtc = null;
        }

        /// <summary>
        /// Whether the row already shows this tip, down to the rendered text. Rewriting it
        /// anyway would raise a change notification and repaint the row for nothing.
        /// </summary>
        public static bool IsAlreadyShowing(ChatItem chat, ChatMessage tip, string renderedPreview)
        {
            if (chat == null || tip == null)
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(tip.Id) &&
                   string.Equals(chat.LastMessageId, tip.Id, StringComparison.Ordinal) &&
                   string.Equals(chat.LastMessage, renderedPreview, StringComparison.Ordinal) &&
                   chat.LastMessageIsFromMe == tip.IsFromMe;
        }

        /// <summary>
        /// Whether the row's strip is showing this outgoing message, and so should follow
        /// its delivery state.
        /// </summary>
        /// <remarks>
        /// The id is the answer whenever the row has one. The clock is only a fallback for
        /// rows an older schema left without an id, and a poor one: two messages sent to the
        /// same chat seconds apart are indistinguishable by time, so a receipt for the older
        /// one would pull the strip back from read to sent. Narrow window, common case.
        /// </remarks>
        public static bool ShowsOutgoingMessage(ChatItem chat, ChatMessage message, TimeSpan clockFallbackWindow)
        {
            if (chat == null || message == null || !message.IsFromMe || !chat.LastMessageIsFromMe)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(chat.LastMessageId) && !string.IsNullOrWhiteSpace(message.Id))
            {
                return string.Equals(chat.LastMessageId, message.Id, StringComparison.Ordinal);
            }

            DateTime messageUtc = ChatMessageOrder.ToComparableUtc(message.Timestamp);
            DateTime stripUtc = chat.LastMessageTimestampUtc.HasValue
                ? ChatMessageOrder.ToComparableUtc(chat.LastMessageTimestampUtc.Value)
                : DateTime.MinValue;

            // With no clock on either side there is nothing to disagree with, so the strip
            // is assumed to be this message rather than left stuck.
            if (messageUtc == DateTime.MinValue || stripUtc == DateTime.MinValue)
            {
                return true;
            }

            TimeSpan apart = stripUtc > messageUtc ? stripUtc - messageUtc : messageUtc - stripUtc;
            return apart <= clockFallbackWindow;
        }

        /// <summary>
        /// Whether a row left without a message id by an older schema should have one
        /// stamped from the tip.
        /// </summary>
        /// <remarks>
        /// Only asked when the preview itself was not replaced, which means the row's text
        /// is already right and only the id is missing. Filling it in avoids a full history
        /// resync to recover something the store already knows. The timestamp guard stops a
        /// stale tip from claiming a strip that has since moved on.
        /// </remarks>
        public static bool ShouldStampMissingMessageId(ChatItem chat, ChatMessage tip)
        {
            if (chat == null || tip == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(chat.LastMessageId) || string.IsNullOrWhiteSpace(tip.Id))
            {
                return false;
            }

            DateTime tipUtc = ChatMessageOrder.ToComparableUtc(tip.Timestamp);
            if (tipUtc == DateTime.MinValue)
            {
                return false;
            }

            DateTime stripUtc = chat.LastMessageTimestampUtc.HasValue
                ? ChatMessageOrder.ToComparableUtc(chat.LastMessageTimestampUtc.Value)
                : DateTime.MinValue;

            return tipUtc >= stripUtc;
        }
    }
}
