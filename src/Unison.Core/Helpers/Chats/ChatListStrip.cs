using System;
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// The chat-list fields one stored message contributes: body, author, kind, send state.
    /// </summary>
    /// <remarks>
    /// Delete, bulk refresh, reconcile and the history tip were each spelling this out. A direct
    /// chat drops the author. A group keeps the other person's name and prefers their phone
    /// address on the strip when the caller already resolved it.
    /// </remarks>
    public sealed class ChatListStrip
    {
        private ChatListStrip(
            string preview,
            DateTime timestamp,
            ChatPreviewKind kind,
            string authorPrefix,
            IList<string> mentionedJids,
            bool isFromMe,
            MessageSendState sendState,
            string messageId,
            string participantJid,
            string senderName)
        {
            Preview = preview;
            Timestamp = timestamp;
            Kind = kind;
            AuthorPrefix = authorPrefix;
            MentionedJids = mentionedJids;
            IsFromMe = isFromMe;
            SendState = sendState;
            MessageId = messageId;
            ParticipantJid = participantJid;
            SenderName = senderName;
        }

        public string Preview { get; }

        public DateTime Timestamp { get; }

        public ChatPreviewKind Kind { get; }

        public string AuthorPrefix { get; }

        public IList<string> MentionedJids { get; }

        public bool IsFromMe { get; }

        public MessageSendState SendState { get; }

        public string MessageId { get; }

        public string ParticipantJid { get; }

        public string SenderName { get; }

        public static ChatListStrip FromMessage(
            ChatMessage message,
            bool isGroup,
            string selfDisplayName,
            string canonicalParticipantJid)
        {
            if (message == null)
            {
                return null;
            }

            string participant = isGroup
                ? ParticipantStripIdentity.PreferForDisplay(message.ParticipantJid, canonicalParticipantJid)
                : null;

            return new ChatListStrip(
                ChatPreviewNormalizer.FormatListPreview(message, isGroup),
                message.Timestamp,
                ChatPreviewNormalizer.InferKindFromMessage(message),
                ChatPreviewNormalizer.FormatListAuthorPrefix(message, isGroup, selfDisplayName, participant),
                message.MentionedJids,
                message.IsFromMe,
                HistoryLiveMessageMapper.FromStatus(message.Status, message.IsFromMe),
                message.Id,
                participant,
                isGroup && !message.IsFromMe ? message.SenderName : null);
        }
    }
}
