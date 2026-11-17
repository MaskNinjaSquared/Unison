namespace Unison.WhatsAppClient.Io.Messages
{
    /// <summary>Client-owned delete-for-me (app-state) send.</summary>
    public sealed class DeleteMessageForMeRequest : MessageTargetRequest
    {
        public bool IsRevoked { get; set; }

        /// <summary>True when the bubble carries downloadable media the patch should clear.</summary>
        public bool DeleteMedia { get; set; }
    }
}
