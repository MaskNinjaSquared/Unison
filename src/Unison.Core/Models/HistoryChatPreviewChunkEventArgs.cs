using System;
using System.Collections.Generic;

namespace Unison.Core.Models
{
    /// <summary>
    /// Raised after a history-sync chunk's list previews were committed to SQLite.
    /// </summary>
    public sealed class HistoryChatPreviewChunkEventArgs : EventArgs
    {
        public string SyncId { get; set; }

        public string SyncType { get; set; }

        public int UpsertedCount { get; set; }

        /// <summary>Rows just written (phase 2 UI hydrate; may be empty if only a count is known).</summary>
        public IReadOnlyList<HistoryChatPreview> Rows { get; set; }
    }
}
