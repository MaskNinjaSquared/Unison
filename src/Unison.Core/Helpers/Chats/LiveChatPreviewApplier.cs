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
        /// <param name="participantJid">
        /// Who wrote the tip in a group (prefer PN when the envelope had participantAlt). Without
        /// this <see cref="ChatAuthorProjection"/> cannot upgrade a bare strip later.
        /// </param>
        /// <param name="senderName">
        /// Best name known for that participant at tip time (bubble / push name). Phone/LID
        /// stand-ins must not overwrite a real name the projection already fixed.
        /// </param>
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
            string yesterdayLabel = null,
            string participantJid = null,
            string senderName = null)
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
                    // Body frozen, but the bubble may have resolved the author after the tip landed.
                    ApplyAuthorParts(chat, participantJid, senderName, authorPrefix);
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

            // Never blank a strip the projection already named on the *same* tip: an empty
            // incoming prefix (sync echo with only a LID) must not wipe "Ana: ". A newer tip
            // with no author still clears — that message has a different sender.
            bool sameTip = !string.IsNullOrWhiteSpace(messageId) &&
                           string.Equals(chat.LastMessageId, messageId, StringComparison.Ordinal);
            bool preserveAuthor = string.IsNullOrEmpty(author) &&
                                  !string.IsNullOrEmpty(chat.LastMessageAuthor) &&
                                  sameTip;
            if (!preserveAuthor)
            {
                chat.LastMessageAuthor = author ?? string.Empty;
            }

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

            ApplyAuthorParts(chat, participantJid, senderName, authorPrefix);
            return true;
        }

        /// <summary>
        /// Copies the tip's sender onto the row so the strip and ChatAuthorProjection share the
        /// same identity the bubble used. A bare LID/phone must not wipe a name the projection
        /// already fixed.
        /// </summary>
        public static void ApplyAuthorParts(
            ChatItem chat,
            string participantJid,
            string senderName,
            string authorPrefix = null)
        {
            if (chat == null)
            {
                return;
            }

            string participant = string.IsNullOrWhiteSpace(participantJid)
                ? null
                : participantJid.Trim();

            bool incomingSenderUsable = GroupParticipantResolver.IsUsableDisplayLabel(
                senderName,
                participant ?? chat.LastMessageParticipantJid);
            // Group title must never label the author strip (legacy sync fallback).
            if (incomingSenderUsable &&
                chat.IsGroup &&
                !string.IsNullOrWhiteSpace(chat.Name) &&
                string.Equals(senderName.Trim(), chat.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                incomingSenderUsable = false;
                senderName = null;
                authorPrefix = null;
            }

            bool existingSenderUsable = GroupParticipantResolver.IsUsableDisplayLabel(
                chat.LastMessageSenderName,
                chat.LastMessageParticipantJid);
            // Drop a strip that was poisoned with the group title so the next pass can fix it.
            if (existingSenderUsable &&
                chat.IsGroup &&
                !string.IsNullOrWhiteSpace(chat.Name) &&
                string.Equals(
                    chat.LastMessageSenderName.Trim(),
                    chat.Name.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                existingSenderUsable = false;
                chat.LastMessageSenderName = null;
                if (!string.IsNullOrEmpty(chat.LastMessageAuthor) &&
                    chat.LastMessageAuthor.StartsWith(chat.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    chat.LastMessageAuthor = string.Empty;
                }
            }

            bool keepExistingSender = !incomingSenderUsable && existingSenderUsable;

            if (keepExistingSender)
            {
                // Projection already named this tip; only fill a blank strip prefix.
                if (string.IsNullOrEmpty(chat.LastMessageAuthor) &&
                    !string.IsNullOrEmpty(authorPrefix))
                {
                    chat.LastMessageAuthor = authorPrefix;
                }

                return;
            }

            if (participant != null)
            {
                chat.LastMessageParticipantJid = participant;
            }

            if (incomingSenderUsable)
            {
                chat.LastMessageSenderName = senderName.Trim();
            }
            else if (participant != null)
            {
                // New tip identity with no name yet — clear a stale name from the previous tip.
                chat.LastMessageSenderName = null;
            }

            if (!string.IsNullOrEmpty(authorPrefix) &&
                !string.Equals(chat.LastMessageAuthor, authorPrefix, StringComparison.Ordinal))
            {
                chat.LastMessageAuthor = authorPrefix;
            }
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
