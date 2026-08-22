using System;

namespace Unison.Core.Models
{
    /// <summary>
    /// One Status item (24h). AuthorJid is the person; the wire chat is always status@broadcast.
    /// </summary>
    public sealed class HistoryStatus : IHistoryMediaFields
    {
        /// <summary>WhatsApp Status TTL, same as Unison.Socket IncomingMessageHandler.</summary>
        public static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

        public string AuthorJid { get; set; }

        public string AuthorLid { get; set; }

        public string AuthorPn { get; set; }

        public string MessageId { get; set; }

        public bool IsFromMe { get; set; }

        public string PushName { get; set; }

        public string Body { get; set; }

        public ChatPreviewKind Kind { get; set; }

        public string MediaUrl { get; set; }

        public string MediaDirectPath { get; set; }

        public string MediaKeyBase64 { get; set; }

        public string MediaFileEncSha256Base64 { get; set; }

        public string MediaMimeType { get; set; }

        public uint MediaDurationSeconds { get; set; }

        public string MediaFileName { get; set; }

        public long MediaFileLengthBytes { get; set; }

        /// <summary>Transient proto thumb bytes; cleared after disk materialize.</summary>
        public byte[] MediaThumbnailJpeg { get; set; }

        /// <summary>Local thumb / full media URI after materialize or download.</summary>
        public string MediaLocalUri { get; set; }

        /// <summary>Local video poster URI.</summary>
        public string MediaPosterUri { get; set; }

        public bool IsVoiceNote { get; set; }

        public DateTime? TimestampUtc { get; set; }

        public DateTime? ExpiresAtUtc { get; set; }

        public string SyncId { get; set; }

        public string SyncType { get; set; }

        public DateTime UpdatedAtUtc { get; set; }

        public bool IsExpired(DateTime utcNow)
        {
            if (!ExpiresAtUtc.HasValue)
            {
                return false;
            }

            return ExpiresAtUtc.Value <= utcNow;
        }
    }
}
