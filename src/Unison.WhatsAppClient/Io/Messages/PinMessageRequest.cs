namespace Unison.WhatsAppClient.Io.Messages
{
    /// <summary>Client-owned pin / unpin send.</summary>
    public sealed class PinMessageRequest : MessageTargetRequest
    {
        public bool Pin { get; set; }

        /// <summary>WhatsApp pin duration; default 7 days when pinning.</summary>
        public uint DurationSeconds { get; set; } = 604800;
    }
}
