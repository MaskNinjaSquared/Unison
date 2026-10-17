namespace Unison.Core.Models
{
    /// <summary>
    /// Platform-agnostic result of picking a chat attachment (image, audio, or document).
    /// </summary>
    public sealed class PickedChatMedia
    {
        public byte[] Bytes { get; set; }
        public string MimeType { get; set; }
        public string FileName { get; set; }
        public bool IsImage { get; set; }
        public bool IsAudio { get; set; }
        public bool IsDocument { get; set; }
    }
}
