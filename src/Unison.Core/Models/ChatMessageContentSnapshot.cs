using System.Collections.Generic;

namespace Unison.Core.Models
{
    /// <summary>
    /// Transport facts for message facade construction
    /// (<c>IMessageService.GetChatMessage</c> / <c>IChatMessageMapper.MapIndividual</c>).
    /// Prefer protocol flags; <see cref="Kind"/> is optional — the mapper resolves it when Text.
    /// </summary>
    public sealed class ChatMessageContentSnapshot
    {
        public string Text { get; set; }
        public ChatMessageKind Kind { get; set; }
        public bool IsImage { get; set; }
        public bool IsVideo { get; set; }
        public bool IsSticker { get; set; }
        public bool IsAudio { get; set; }
        public bool IsVoice { get; set; }
        public bool IsDocument { get; set; }
        public string Caption { get; set; }
        public string QuotedText { get; set; }
        /// <summary>Media kind for the quoted bubble strip (Text = plain quote body).</summary>
        public ChatPreviewKind QuotedKind { get; set; }
        public string QuotedSenderName { get; set; }
        /// <summary>WhatsApp ContextInfo.Participant of the quoted message (group author JID).</summary>
        public string QuotedParticipantJid { get; set; }
        /// <summary>WhatsApp ContextInfo.StanzaId of the quoted message (when present).</summary>
        public string QuotedMessageId { get; set; }
        public List<string> MentionedJids { get; set; }

        /// <summary>ContextInfo.isForwarded when the message was forwarded in WhatsApp.</summary>
        public bool IsForwarded { get; set; }
    }
}
