using System;

namespace Unison.Core.Models
{
    /// <summary>
    /// Live or offline tip refresh after an incoming envelope lands on the timeline
    /// (insert, duplicate, or alias consolidate). Offline records a replay tip;
    /// online refreshes the list strip immediately.
    /// </summary>
    public sealed class IncomingEnvelopePreviewRequest
    {
        public string Jid { get; set; }
        public string Preview { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsGroup { get; set; }
        public bool IsFromMe { get; set; }
        public bool IsOffline { get; set; }

        /// <summary>False for a duplicate: tip refresh without adding to the badge.</summary>
        public bool CountUnread { get; set; }

        /// <summary>
        /// When offline, also mark the chat dirty on the pending persist queue
        /// (duplicate / alias paths). Fresh inserts skip this.
        /// </summary>
        public bool MarkDirtyWhenOffline { get; set; }

        public ChatPreviewKind? Kind { get; set; }
        public string Status { get; set; }
        public string AuthorPrefix { get; set; }
        public string ParticipantJid { get; set; }
        public string SenderName { get; set; }
    }
}
