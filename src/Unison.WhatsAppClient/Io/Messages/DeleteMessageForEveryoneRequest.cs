namespace Unison.WhatsAppClient.Io.Messages
{
    /// <summary>Client-owned revoke-for-everyone send.</summary>
    public sealed class DeleteMessageForEveryoneRequest : MessageTargetRequest
    {
        public bool IsRevoked { get; set; }

        public bool IsGroupChat { get; set; }

        public bool ViewerIsGroupAdmin { get; set; }
    }
}
