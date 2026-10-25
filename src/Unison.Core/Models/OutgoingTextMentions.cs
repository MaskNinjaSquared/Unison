using System.Collections.Generic;

namespace Unison.Core.Models
{
    /// <summary>
    /// Text as the transport expects it: @number in the body, JIDs beside it,
    /// and <see cref="MentionAll"/> when the composer mentioned everyone.
    /// </summary>
    public sealed class OutgoingTextMentions
    {
        public string Text { get; set; }

        public List<string> Mentions { get; set; }

        public bool MentionAll { get; set; }

        /// <summary>The message this send replies to. Null when it replies to nobody.</summary>
        public OutgoingQuote Quote { get; set; }

        /// <summary>True when this send is a user Forward (ContextInfo.isForwarded).</summary>
        public bool MarkAsForwarded { get; set; }

        /// <summary>Prior forwardingScore + 1 when re-forwarding; 0 lets the factory use 1.</summary>
        public uint ForwardingScore { get; set; }
    }
}
