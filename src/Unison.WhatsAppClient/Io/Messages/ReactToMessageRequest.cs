namespace Unison.WhatsAppClient.Io.Messages
{
    /// <summary>Client-owned reaction send. Empty <see cref="Emoji"/> removes the reaction.</summary>
    public sealed class ReactToMessageRequest : MessageTargetRequest
    {
        public string Emoji { get; set; }
    }
}
