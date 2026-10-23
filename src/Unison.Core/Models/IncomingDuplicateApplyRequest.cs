using System;

namespace Unison.Core.Models
{
    /// <summary>
    /// Post-<c>AcceptIncomingTimeline</c> duplicate follow-up: persist when the existing
    /// bubble changed, clear missing-message ledger, refresh tip.
    /// </summary>
    public sealed class IncomingDuplicateApplyRequest
    {
        public string ChatJid { get; set; }
        public ChatMessage IncomingMessage { get; set; }
        public IncomingTimelineAcceptResult Timeline { get; set; }
        public string DisplayContent { get; set; }
        public bool IsOffline { get; set; }
        public bool IsGroup { get; set; }
        public bool IsFromMe { get; set; }
        public ChatPreviewKind PreviewKind { get; set; }
        public string ListAuthorPrefix { get; set; }
        public string StripParticipantJid { get; set; }
        public string SenderName { get; set; }
    }
}
