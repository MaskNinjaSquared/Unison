// =============================================================================
// HistoryChatPreviewHydrate
//
// One slice of history-sync chat previews applied onto the in-memory list.
// The chat façade owns the apply; the list ViewModel only reacts for UI batching.
// =============================================================================
using System.Collections.Generic;

namespace Unison.Core.Models
{
    public sealed class HistoryChatPreviewHydrateRequest
    {
        public IReadOnlyList<HistoryChatPreview> Rows { get; set; }

        /// <summary>Localized "Yesterday" label for strip timestamps.</summary>
        public string YesterdayLabel { get; set; }

        /// <summary>Localized self name for "You:" author prefixes.</summary>
        public string SelfDisplayName { get; set; }
    }

    public sealed class HistoryChatPreviewHydrateResult
    {
        public int CreatedCount { get; set; }

        public int UpdatedCount { get; set; }

        /// <summary>New rows added to the chat store; the list VM wires PropertyChanged.</summary>
        public IReadOnlyList<ChatItem> CreatedChats { get; set; }
    }
}
