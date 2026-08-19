using System;
using System.Collections.Generic;

namespace Unison.Core.Models
{
    /// <summary>
    /// Raised after history message rows from a chunk were committed to SQLite.
    /// </summary>
    public sealed class HistoryMessageChunkEventArgs : EventArgs
    {
        public string SyncId { get; set; }

        public string SyncType { get; set; }

        public int UpsertedCount { get; set; }

        public int ConversationCount { get; set; }

        /// <summary>Distinct chat JIDs touched by this upsert (for open-detail hydrate).</summary>
        public IReadOnlyList<string> ChatJids { get; set; }
    }
}
