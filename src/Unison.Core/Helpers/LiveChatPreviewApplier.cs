// =============================================================================
// LiveChatPreviewApplier
//
// Updates the chat-list strip when a live (or offline-replay) tip is not older
// than what the row already shows. Same job HistoryChatPreviewApplier does for
// history chunks; this one takes the loose fields the pump already has in hand
// rather than a HistoryChatPreview DTO.
//
// Extracted from WhatsAppService.ApplyChatPreviewIfNewer so 3.10 can move the
// apply without dragging the UI thread and toast with it.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Mappers;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class LiveChatPreviewApplier
    {
        /// <summary>
        /// Writes the strip when the candidate is allowed. Returns false when the tip is stale
        /// or the row already shows the same tip with the same body.
        /// </summary>
        public static bool ApplyIfNewer(
            ChatItem chat,
            string preview,
            DateTime timestamp,
            bool force = false,
            ChatPreviewKind? kindHint = null,
            string authorPrefix = null,
            IList<string> mentionedJids = null,
            bool? isFromMe = null,
            MessageSendState? sendState = null,
            string messageId = null,
            string yesterdayLabel = null)
        {
            if (chat == null)
            {
                return false;
            }

            DateTime candidateUtc = ChatMessageOrder.ToComparableUtc(timestamp);
            DateTime currentUtc = chat.LastMessageTimestampUtc.HasValue
                ? ChatMessageOrder.ToComparableUtc(chat.LastMessageTimestampUtc.Value)
                : DateTime.MinValue;

            if (!ChatPreviewStaleness.ShouldAccept(chat.LastMessageTimestampUtc, timestamp, force))
            {
                return false;
            }

            bool sameId = !string.IsNullOrWhiteSpace(messageId) &&
                          string.Equals(chat.LastMessageId, messageId, StringComparison.Ordinal);
            if (!force &&
                sameId &&
                candidateUtc == currentUtc &&
                isFromMe.HasValue &&
                chat.LastMessageIsFromMe == isFromMe.Value &&
                sendState.HasValue &&
                chat.LastMessageSendState == sendState.Value)
            {
                string peekRaw = preview ?? string.Empty;
                ChatPreviewNormalizer.Normalize(peekRaw, kindHint, out _, out var peekClean);
                if (string.Equals(chat.LastMessage, peekClean, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            string raw = preview ?? string.Empty;
            string author = authorPrefix ?? string.Empty;
            if (string.IsNullOrEmpty(author))
            {
                ChatPreviewNormalizer.TryPeelAuthorPrefix(ref raw, out author);
            }

            if (kindHint == null &&
                raw.IndexOf("[Document]", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                kindHint = ChatPreviewKind.Document;
            }

            ChatPreviewNormalizer.Normalize(raw, kindHint, out var kind, out var cleanPreview);

            chat.LastMessageAuthor = author ?? string.Empty;
            chat.LastMessage = cleanPreview;
            chat.LastMessageKind = kind;
            chat.LastMessageMentionedJids = mentionedJids != null && mentionedJids.Count > 0
                ? new List<string>(mentionedJids)
                : null;
            if (isFromMe.HasValue)
            {
                chat.LastMessageIsFromMe = isFromMe.Value;
            }

            if (sendState.HasValue)
            {
                chat.LastMessageSendState = sendState.Value;
            }
            else if (isFromMe == false)
            {
                chat.LastMessageSendState = MessageSendState.NotApplicable;
            }
            else if (isFromMe == true && chat.LastMessageSendState == MessageSendState.NotApplicable)
            {
                chat.LastMessageSendState = MessageSendState.Pending;
            }

            if (!string.IsNullOrWhiteSpace(messageId))
            {
                chat.LastMessageId = messageId;
            }

            chat.Timestamp = timestamp == DateTime.MinValue
                ? string.Empty
                : WhatsAppMapper.FormatTimestamp(timestamp, yesterdayLabel);
            chat.LastMessageTimestampUtc = candidateUtc == DateTime.MinValue
                ? (DateTime?)null
                : candidateUtc;
            return true;
        }

        /// <summary>
        /// Kind for the list strip: prefer a non-text media reading from the envelope, else infer
        /// from the domain message.
        /// </summary>
        public static ChatPreviewKind ResolveKind(ChatMessage message, MessageRenderInfo renderInfo)
        {
            if (renderInfo != null)
            {
                ChatPreviewKind fromRender = renderInfo.PreviewKind;
                if (fromRender != ChatPreviewKind.Text)
                {
                    return fromRender;
                }
            }

            return ChatPreviewNormalizer.InferKindFromMessage(message);
        }
    }
}
