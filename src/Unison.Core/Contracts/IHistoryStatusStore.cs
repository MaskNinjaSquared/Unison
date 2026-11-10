using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// SQLite Status items (status@broadcast), keyed by author JID — not chat-list rows.
    /// </summary>
    public interface IHistoryStatusStore
    {
        int SchemaVersion { get; }

        /// <summary>Raised after a write that changed rows (upsert, clear, expiry delete).</summary>
        event EventHandler Changed;

        Task InitializeAsync();

        Task UpsertManyAsync(IReadOnlyList<HistoryStatus> rows);

        /// <summary>Non-expired items, newest first.</summary>
        Task<IReadOnlyList<HistoryStatus>> GetActiveAsync(int limit = 200);

        Task<IReadOnlyList<HistoryStatus>> GetActiveForAuthorAsync(string authorJid, int limit = 50);

        /// <summary>Marks one Status item viewed on this device. No-op when already viewed.</summary>
        Task<bool> MarkViewedAsync(string authorJid, string messageId);

        /// <summary>
        /// Records that <paramref name="viewerJid"/> viewed the account's own Status item.
        /// One row per (item, viewer); false when that viewer was already counted.
        /// </summary>
        Task<bool> RecordViewerAsync(string messageId, string viewerJid, DateTime viewedAtUtc);

        /// <summary>Distinct viewers per message id; ids without viewers are absent.</summary>
        Task<IReadOnlyDictionary<string, int>> GetViewerCountsAsync(IReadOnlyList<string> messageIds);

        Task<int> DeleteExpiredAsync();

        /// <summary>
        /// Removes one item the account sent. The outgoing copy is ingested optimistically from
        /// the local echo, long before the server acks, so a refused Status has to be taken back
        /// out or it sits in "My status" as though it had been delivered.
        /// </summary>
        Task<bool> DeleteOwnItemAsync(string messageId);

        Task ClearAsync(string reason = null);
    }
}
