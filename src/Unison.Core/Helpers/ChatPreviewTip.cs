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
