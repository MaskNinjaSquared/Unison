using System;
using SQLite;
using Unison.Core.Models;

namespace Unison.Uwp.Data.Entities
{
    /// <summary>
    /// History message row (<c>history_message</c>). PK = ChatJid + MessageId.
    /// </summary>
    [Table("history_message")]
    public sealed class HistoryMessageRow
    {
        /// <summary>Composite key: chatJid + "\u001f" + messageId.</summary>
        [PrimaryKey]
        public string Id { get; set; }

        [Indexed]
        public string ChatJid { get; set; }

        public string MessageId { get; set; }

        public bool IsFromMe { get; set; }

        public string ParticipantJid { get; set; }

        public string SenderName { get; set; }

        public string Body { get; set; }

        /// <summary><see cref="ChatPreviewKind"/> as INTEGER.</summary>
        public int Kind { get; set; }

        /// <summary><see cref="MessageSendState"/> as INTEGER.</summary>
        public int SendState { get; set; }

        public string MediaUrl { get; set; }

        public string MediaDirectPath { get; set; }

        public string MediaKeyBase64 { get; set; }

        public string MediaFileEncSha256Base64 { get; set; }

        public string MediaMimeType { get; set; }

        public uint MediaDurationSeconds { get; set; }

        public string MediaFileName { get; set; }

        public long MediaFileLengthBytes { get; set; }

        public string MediaThumbnailBase64 { get; set; }

        public bool IsVoiceNote { get; set; }

        public bool IsRevoked { get; set; }

        public bool IsForwarded { get; set; }

        public bool IsPinned { get; set; }

        public DateTime? PinnedAtUtc { get; set; }

        public DateTime? PinExpiresAtUtc { get; set; }

        public string QuotedMessageId { get; set; }

        public string QuotedChatJid { get; set; }

        public string QuotedParticipantJid { get; set; }

        public string QuotedSenderName { get; set; }

        public string QuotedBody { get; set; }

        /// <summary><see cref="ChatPreviewKind"/> as INTEGER.</summary>
        public int QuotedKind { get; set; }

        public string MediaLocalUri { get; set; }

        public string MediaPosterUri { get; set; }

        /// <summary>Comma-separated mentioned JIDs.</summary>
        public string MentionedJids { get; set; }

        [Indexed]
        public DateTime? TimestampUtc { get; set; }

        public string SyncId { get; set; }

        public string SyncType { get; set; }

        public DateTime UpdatedAtUtc { get; set; }

        public static string MakeId(string chatJid, string messageId)
        {
            return (chatJid ?? string.Empty) + "\u001f" + (messageId ?? string.Empty);
        }
    }
}
