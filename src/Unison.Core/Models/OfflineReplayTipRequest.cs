using System;

namespace Unison.Core.Models
{
    /// <summary>One replayed message as the chat list sees it, before it is folded into a tip.</summary>
    public sealed class OfflineReplayTipRequest
    {
        public string Jid { get; set; }
        public string Preview { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsGroup { get; set; }
        public bool IsFromMe { get; set; }

        /// <summary>False for a duplicate: it refreshes the tip without adding to the badge.</summary>
        public bool CountUnread { get; set; }

        public ChatPreviewKind Kind { get; set; } = ChatPreviewKind.Text;
        public string Status { get; set; }
        public string AuthorPrefix { get; set; }
        public string ParticipantJid { get; set; }
        public string SenderName { get; set; }
    }
}
