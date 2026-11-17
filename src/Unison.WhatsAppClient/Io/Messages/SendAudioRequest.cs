namespace Unison.WhatsAppClient.Io.Messages
{
    /// <summary>Client-owned audio / voice-note send input.</summary>
    public sealed class SendAudioRequest
    {
        public string ChatJid { get; set; }

        public byte[] AudioBytes { get; set; }

        public string MimeType { get; set; }

        public uint DurationSeconds { get; set; }

        public bool IsVoiceMessage { get; set; }

        public bool ViewOnce { get; set; }
    }
}
