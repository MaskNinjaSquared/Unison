// =============================================================================
// IncomingChatMessageSnapshot
//
// Builds the ChatMessageContentSnapshot the incoming pump hands to
// MessageFacade.GetChatMessage. Kept out of the model type so Models does not
// depend on Helpers (MessageRenderInfo).
// =============================================================================
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class IncomingChatMessageSnapshot
    {
        /// <summary>
        /// Media flags and caption come from <paramref name="renderInfo"/> when present;
        /// quote/forward fields are already resolved by the caller.
        /// </summary>
        public static ChatMessageContentSnapshot FromRender(
            MessageRenderInfo renderInfo,
            string text,
            bool isForwarded,
            string quotedText,
            ChatPreviewKind quotedKind,
            string quotedSenderName,
            string quotedParticipantJid,
            string quotedMessageId,
            List<string> mentionedJids)
        {
            return new ChatMessageContentSnapshot
            {
                Text = text,
                IsImage = renderInfo?.IsImage == true,
                IsVideo = renderInfo?.IsVideo == true,
                IsSticker = renderInfo?.IsSticker == true,
                IsAudio = renderInfo?.IsAudio == true,
                IsVoice = renderInfo?.IsVoice == true,
                IsDocument = renderInfo?.IsDocument == true,
                Caption = renderInfo?.Caption ?? string.Empty,
                IsForwarded = isForwarded,
                QuotedText = quotedText,
                QuotedKind = quotedKind,
                QuotedSenderName = quotedSenderName,
                QuotedParticipantJid = quotedParticipantJid,
                QuotedMessageId = quotedMessageId,
                MentionedJids = mentionedJids
            };
        }
    }
}
