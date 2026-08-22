using System;
using SQLite;
using Unison.Core.Models;

namespace Unison.Uwp.Data.Entities
{
    [Table("history_status")]
    public sealed class HistoryStatusRow
    {
        [PrimaryKey]
        public string Id { get; set; }

        [Indexed]
        public string AuthorJid { get; set; }

        public string AuthorLid { get; set; }

        public string AuthorPn { get; set; }

        public string MessageId { get; set; }

        public bool IsFromMe { get; set; }

        public string PushName { get; set; }

        public string Body { get; set; }

        public int Kind { get; set; }

        public string MediaUrl { get; set; }

        public string MediaDirectPath { get; set; }

        public string MediaKeyBase64 { get; set; }

        public string MediaFileEncSha256Base64 { get; set; }

        public string MediaMimeType { get; set; }

        public uint MediaDurationSeconds { get; set; }

        public string MediaFileName { get; set; }

        public long MediaFileLengthBytes { get; set; }

        public string MediaLocalUri { get; set; }

        public string MediaPosterUri { get; set; }

        public bool IsVoiceNote { get; set; }

        [Indexed]
        public DateTime? TimestampUtc { get; set; }

        [Indexed]
        public DateTime? ExpiresAtUtc { get; set; }

        public string SyncId { get; set; }

        public string SyncType { get; set; }

        public DateTime UpdatedAtUtc { get; set; }

        public static string MakeId(string authorJid, string messageId)
        {
            return (authorJid ?? string.Empty) + "\u001f" + (messageId ?? string.Empty);
        }
    }
}
