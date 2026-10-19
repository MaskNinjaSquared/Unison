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
    }
}
