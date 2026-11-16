using System;
using System.Collections.Generic;

namespace Unison.Core.Models
{
    /// <summary>What a <c>&lt;receipt&gt;</c> node says, once.</summary>
    public sealed class ReceiptFacts
    {
        public string Status { get; set; }

        /// <summary>The root id plus every <c>item</c> descendant; a receipt can ack a batch.</summary>
        public ICollection<string> MessageIds { get; set; }

        public string ChatJid { get; set; }

        /// <summary>Canonical, and empty for direct chats.</summary>
        public string Participant { get; set; }

        public bool IsGroup { get; set; }

        /// <summary>The raw <c>type</c> attribute ("read", "played", "read-self", …).</summary>
        public string ReceiptType { get; set; }

        /// <summary>The <c>t</c> attribute; null when absent or unparsable.</summary>
        public DateTime? TimestampUtc { get; set; }
    }
}
