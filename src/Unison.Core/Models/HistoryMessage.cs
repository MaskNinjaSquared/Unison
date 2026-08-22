using System;
using System.Collections.Generic;

namespace Unison.Core.Models
{
    /// <summary>
    /// History message row for SQLite <c>history_message</c>.
    /// Not a full <see cref="ChatMessage"/> — enough to rebuild the timeline bubble.
    /// </summary>
    public sealed class HistoryMessage : IHistoryMediaFields
    {
        public string ChatJid { get; set; }

        public string MessageId { get; set; }

        public bool IsFromMe { get; set; }

        public string ParticipantJid { get; set; }

        public string SenderName { get; set; }

        public string Body { get; set; }

        public ChatPreviewKind Kind { get; set; }

        public MessageSendState SendState { get; set; }

        /// <summary>CDN / MMG URL (image, video, audio, document, sticker).</summary>
        public string MediaUrl { get; set; }

        /// <summary>WhatsApp directPath for MMG download when URL is stale.</summary>
        public string MediaDirectPath { get; set; }

        public string MediaKeyBase64 { get; set; }

        public string MediaFileEncSha256Base64 { get; set; }

        public string MediaMimeType { get; set; }

        /// <summary>Audio/video duration in seconds.</summary>
        public uint MediaDurationSeconds { get; set; }

        /// <summary>Document original file name.</summary>
        public string MediaFileName { get; set; }

        public long MediaFileLengthBytes { get; set; }

        /// <summary>Transient proto thumb bytes; cleared after disk materialize.</summary>
        public byte[] MediaThumbnailJpeg { get; set; }

        /// <summary>True when audio is a PTT voice note.</summary>
        public bool IsVoiceNote { get; set; }

        public bool IsRevoked { get; set; }

        /// <summary>ContextInfo.isForwarded — independent of <see cref="Kind"/>.</summary>
        public bool IsForwarded { get; set; }

        public bool IsPinned { get; set; }

        public DateTime? PinnedAtUtc { get; set; }

        public DateTime? PinExpiresAtUtc { get; set; }

        public string QuotedMessageId { get; set; }

        public string QuotedChatJid { get; set; }

        public string QuotedParticipantJid { get; set; }

        public string QuotedSenderName { get; set; }

        public string QuotedBody { get; set; }

        public ChatPreviewKind QuotedKind { get; set; }

        /// <summary>Local file URI after on-demand download.</summary>
        public string MediaLocalUri { get; set; }

        /// <summary>Local video poster URI after on-demand download.</summary>
        public string MediaPosterUri { get; set; }

        /// <summary>Full reactor rows for this message (<c>history_message_reaction</c>).</summary>
        public List<HistoryMessageReaction> Reactions { get; set; }

        /// <summary>ContextInfo mentioned JIDs (@number in the body).</summary>
        public List<string> MentionedJids { get; set; }

        public DateTime? TimestampUtc { get; set; }

        public string SyncId { get; set; }

        public string SyncType { get; set; }

        public DateTime UpdatedAtUtc { get; set; }
    }
}
