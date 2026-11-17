namespace Unison.WhatsAppClient.Io.Messages
{
    /// <summary>Client-owned document send input.</summary>
    public sealed class SendDocumentRequest
    {
        public string ChatJid { get; set; }

        public byte[] DocumentBytes { get; set; }

        public string MimeType { get; set; }

        public string FileName { get; set; }

        public string Caption { get; set; }
    }
}
