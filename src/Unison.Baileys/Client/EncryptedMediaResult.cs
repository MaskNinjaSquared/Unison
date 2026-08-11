namespace Unison.Baileys.Client
{
    /// <summary>
    /// Result of encrypting a media file for WhatsApp upload.
    /// </summary>
    public struct EncryptedMediaResult
    {
        public byte[] MediaKey { get; set; }
        public byte[] EncryptedBytes { get; set; }
        public byte[] Mac { get; set; }
        public byte[] FileSha256 { get; set; }
        public byte[] FileEncSha256 { get; set; }
        public long FileLength { get; set; }
    }
}
