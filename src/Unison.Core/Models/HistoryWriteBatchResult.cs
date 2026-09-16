using System;
using System.Collections.Generic;

namespace Unison.Core.Models
{
    /// <summary>
    /// Outcome of <see cref="Contracts.IHistoryMessageStore.PersistWriteBatchAsync"/>.
    /// </summary>
    public sealed class HistoryWriteBatchResult
    {
        public int UpsertedCount { get; set; }

        /// <summary>
        /// Chats that got a new body and/or a pin/reaction/revoke side effect.
        /// </summary>
        public IReadOnlyList<string> ChatJids { get; set; } = Array.Empty<string>();
    }
}
