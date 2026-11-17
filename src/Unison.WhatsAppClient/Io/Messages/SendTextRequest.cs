using System.Collections.Generic;

namespace Unison.WhatsAppClient.Io.Messages
{
    /// <summary>
    /// Client-owned text-send input. Core <c>OutgoingTextMentions</c> maps to this at the façade.
    /// </summary>
    public sealed class SendTextRequest
    {
        public string ChatJid { get; set; }

        public string Text { get; set; }

        public List<string> Mentions { get; set; }

        public bool MentionAll { get; set; }

        /// <summary>Null when this send quotes nothing.</summary>
        public OutgoingQuoteRequest Quote { get; set; }

        public bool MarkAsForwarded { get; set; }

        /// <summary>Prior forwardingScore + 1 when re-forwarding; 0 lets the factory use 1.</summary>
        public uint ForwardingScore { get; set; }
    }
}
