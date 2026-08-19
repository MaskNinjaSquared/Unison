using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// SQLite history messages + per-reactor reactions (<c>history_message</c> / <c>history_message_reaction</c>).
    /// </summary>
    public interface IHistoryMessageStore
    {
        int SchemaVersion { get; }

        Task InitializeAsync();

        Task UpsertManyAsync(IReadOnlyList<HistoryMessage> rows);

        Task PersistWriteBatchAsync(HistoryMessageWriteBatch batch);

        /// <summary>Live send/receive/outbox: upsert bodies and replace reactions for those ids.</summary>
        Task UpsertLiveMessagesAsync(string chatJid, IReadOnlyList<ChatMessage> messages);

        Task<HistoryMessage> GetAsync(string chatJid, string messageId);

        /// <summary>
        /// Newest-first page, returned chronological. When <paramref name="beforeUtc"/> is set,
        /// rows strictly older than that cursor (load-more).
        /// </summary>
        Task<IReadOnlyList<HistoryMessage>> GetForChatAsync(
            string chatJid,
            int limit = 100,
            DateTime? beforeUtc = null,
            string beforeMessageId = null);

        Task<IReadOnlyList<HistoryMessage>> GetPinnedForChatAsync(string chatJid, int maxCount = 3);

        Task<IReadOnlyList<HistoryMessage>> GetPendingOutgoingAsync(string chatJid);

        /// <summary>
        /// Media and document rows only (no text / reactions / stickers), newest first.
        /// Feeds the chat-info Media / Files panes, which must not be crowded out by text rows.
        /// </summary>
        Task<IReadOnlyList<HistoryMessage>> GetMediaForChatAsync(string chatJid, int limit = 400);

        Task<int> CountAsync(string syncId = null);

        Task ClearAsync(string reason = null);

        event EventHandler<HistoryMessageChunkEventArgs> ChunkPersisted;
    }
}
