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

        /// <summary>
        /// Additive reactor rows, no message bodies (live reaction envelope). An empty emoji removes
        /// that reactor. Needed because a chip-summary bubble cannot carry the rows through
        /// <see cref="UpsertLiveMessagesAsync"/>.
        /// </summary>
        Task UpsertReactionsAsync(IReadOnlyList<HistoryMessageReaction> reactions);

        /// <summary>
        /// Pin/unpin flags on existing <c>history_message</c> rows (live pin envelope or local
        /// long-press). Does not insert missing bodies — only updates rows that already exist.
        /// </summary>
        Task UpsertPinsAsync(IReadOnlyList<HistoryMessagePinUpdate> pins);

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

        /// <summary>
        /// Same as <see cref="GetForChatAsync"/> but for every SQLite key a conversation may use
        /// (PN / LID / canonical). One query with <c>ChatJid IN (...)</c> instead of N round-trips.
        /// </summary>
        Task<IReadOnlyList<HistoryMessage>> GetForChatKeysAsync(
            IReadOnlyList<string> chatJids,
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

        /// <summary>
        /// Newest listable row per chat (no reactions). When <paramref name="chatJids"/> is null
        /// or empty, every chat in SQLite is considered — used to repair list previews on startup.
        /// </summary>
        Task<IReadOnlyList<HistoryMessage>> GetNewestPerChatAsync(IReadOnlyList<string> chatJids = null);

        /// <summary>
        /// Full reactor rows for one message (reactions dialog). Uses explicit columns — not <c>SELECT *</c>.
        /// </summary>
        Task<IReadOnlyList<HistoryMessageReaction>> GetReactionsForMessageAsync(
            string chatJid,
            string messageId);

        Task<int> CountAsync(string syncId = null);

        Task ClearAsync(string reason = null);

        event EventHandler<HistoryMessageChunkEventArgs> ChunkPersisted;
    }
}
