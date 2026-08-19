using System;
using System.Collections.Generic;

namespace Unison.Core.Models
{
    /// <summary>
    /// Result of persisting one history-sync chunk to SQLite (previews + messages).
    /// </summary>
    public sealed class HistorySqliteChunkResult
    {
        public string SyncType { get; set; }

        public int ConversationCount { get; set; }

        public int PreviewUpserted { get; set; }

        public int MessageUpserted { get; set; }

        /// <summary>Chats that received message rows (for open-detail hydrate).</summary>
        public IReadOnlyList<string> MessageChatJids { get; set; } = Array.Empty<string>();

        /// <summary>All conversation JIDs in the chunk (on-demand latch clear).</summary>
        public IReadOnlyList<string> ConversationJids { get; set; } = Array.Empty<string>();

        public bool IsOnDemand
        {
            get
            {
                return !string.IsNullOrEmpty(SyncType) &&
                       SyncType.IndexOf("OnDemand", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
    }
}
