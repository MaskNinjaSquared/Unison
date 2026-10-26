using System;

namespace Unison.Core.Models
{
    /// <summary>
    /// Post-<c>AcceptIncomingTimeline</c> alias-consolidate follow-up: durable finalize
    /// then tip refresh (same tip path as a duplicate).
    /// </summary>
    public sealed class IncomingAliasConsolidatedApplyRequest
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
