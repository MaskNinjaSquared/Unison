namespace Unison.WhatsAppClient.Io.Messages
{
    /// <summary>
    /// The message a send is replying to, as the client I/O surface sees it.
    /// <see cref="Kind"/> is the preview-kind name (e.g. "Text"), not a Core enum.
    /// </summary>
    public sealed class OutgoingQuoteRequest
    {
        public string MessageId { get; set; }

        public string ChatJid { get; set; }

        public string ParticipantJid { get; set; }

        /// <summary>Display name for the optimistic strip. The wire uses <see cref="ParticipantJid"/>.</summary>
        public string AuthorName { get; set; }

        public bool FromMe { get; set; }

        /// <summary>Chat preview kind name; empty means text.</summary>
        public string Kind { get; set; }

        public string Text { get; set; }
    }
}
