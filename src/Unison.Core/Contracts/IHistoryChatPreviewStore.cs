using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// SQLite list-preview rows from history sync (phase 1: write off-thread; UI read is phase 2).
    /// </summary>
    public interface IHistoryChatPreviewStore
    {
        /// <summary>Schema version of <c>history_chat_preview</c> (bump when columns change).</summary>
        int SchemaVersion { get; }

        Task InitializeAsync();

        /// <summary>Insert or replace rows; raises <see cref="ChunkPersisted"/> when <paramref name="notifyChunk"/> is true.</summary>
        Task UpsertManyAsync(IReadOnlyList<HistoryChatPreview> rows, bool notifyChunk = true);

        Task<IReadOnlyList<HistoryChatPreview>> GetAllAsync(string syncId = null);

        Task<int> CountAsync(string syncId = null);

        /// <summary>Clears all preview rows (wipe / resync epoch rotate).</summary>
        Task ClearAsync(string reason = null);

        event EventHandler<HistoryChatPreviewChunkEventArgs> ChunkPersisted;
    }
}
