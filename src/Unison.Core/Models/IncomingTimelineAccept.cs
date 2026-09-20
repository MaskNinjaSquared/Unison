// =============================================================================
// IncomingTimelineAccept
//
// Result of trying to put a live/offline ChatMessage into the in-memory
// timeline: insert, same-chat enrich, or alias-linked detect/consolidate.
// =============================================================================
namespace Unison.Core.Models
{
    public enum IncomingTimelineAcceptKind
    {
        Inserted = 0,
        DuplicateSameChat,
        AliasLinkedDuplicate,
        AliasConsolidated
    }

    public sealed class IncomingTimelineAcceptResult
    {
        public IncomingTimelineAcceptKind Kind { get; set; }
        public ChatMessage Message { get; set; }
        public bool ExistingChanged { get; set; }
        public string AliasSourceChatJid { get; set; }
        public ChatMessage ConsolidatedMessage { get; set; }
    }
}
