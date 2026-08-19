namespace Unison.Core.Models
{
    /// <summary>Download envelope copied from history protobuf onto SQLite rows.</summary>
    public interface IHistoryMediaFields
    {
        string MediaUrl { get; set; }

        string MediaDirectPath { get; set; }

        string MediaKeyBase64 { get; set; }

        string MediaFileEncSha256Base64 { get; set; }

        string MediaMimeType { get; set; }

        uint MediaDurationSeconds { get; set; }

        string MediaFileName { get; set; }

        long MediaFileLengthBytes { get; set; }

        string MediaThumbnailBase64 { get; set; }

        bool IsVoiceNote { get; set; }
    }
}
