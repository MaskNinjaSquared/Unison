using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SQLite;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Uwp.Data.Entities;
using Windows.Storage;

namespace Unison.Uwp.Data
{
    /// <summary>
    /// SQLite <c>history_message</c> + <c>history_message_reaction</c> (same <c>unison.db</c>).
    /// </summary>
    public sealed class HistoryMessageStore : IHistoryMessageStore
    {
        private static readonly string DatabaseFileName = "unison.db";

        public const int CurrentSchemaVersion = 7;

        /// <summary>
        /// Timeline open / load-more: protocol thumbs live on disk (<c>MediaLocalUri</c> / poster).
        /// </summary>
        private const string TimelineSelectSql =
            "SELECT Id, ChatJid, MessageId, IsFromMe, ParticipantJid, SenderName, Body, Kind, SendState, " +
            "MediaUrl, MediaDirectPath, MediaKeyBase64, MediaFileEncSha256Base64, MediaMimeType, " +
            "MediaDurationSeconds, MediaFileName, MediaFileLengthBytes, IsVoiceNote, IsRevoked, IsForwarded, IsPinned, " +
            "PinnedAtUtc, PinExpiresAtUtc, QuotedMessageId, QuotedChatJid, QuotedParticipantJid, " +
            "QuotedSenderName, QuotedBody, QuotedKind, MediaLocalUri, MediaPosterUri, MentionedJids, " +
            "TimestampUtc, SyncId, SyncType, UpdatedAtUtc FROM history_message ";

        private const string ReactionDetailSelectSql =
            "SELECT ChatJid, MessageId, ReactorJid, ReactorName, Emoji, FromMe, ReactionMessageId, TimestampUtc " +
            "FROM history_message_reaction ";

        private readonly SemaphoreSlim _initLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        private SQLiteAsyncConnection _connection;
        private bool _initialized;

        public int SchemaVersion => CurrentSchemaVersion;

        public event EventHandler<HistoryMessageChunkEventArgs> ChunkPersisted;

        public async Task InitializeAsync()
        {
            if (_initialized)
            {
                return;
            }

            await _initLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_initialized)
                {
                    return;
                }

                SQLitePCL.Batteries.Init();
                string dbPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, DatabaseFileName);
                _connection = new SQLiteAsyncConnection(dbPath);
                await _connection.CreateTableAsync<HistoryMessageRow>().ConfigureAwait(false);
                await _connection.CreateTableAsync<HistoryMessageReactionRow>().ConfigureAwait(false);
                await DropLegacyThumbnailColumnAsync().ConfigureAwait(false);
                await EnsureIndexesAsync().ConfigureAwait(false);
                _initialized = true;
                Debug.WriteLine("[HistoryMessageStore] Initialized schema=" + CurrentSchemaVersion + " at " + dbPath);
            }
            finally
            {
                _initLock.Release();
            }
        }

        public Task UpsertManyAsync(IReadOnlyList<HistoryMessage> rows)
        {
            var batch = new HistoryMessageWriteBatch();
            if (rows != null)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    if (rows[i] != null)
                    {
                        batch.Messages.Add(rows[i]);
                    }
                }
            }

            return PersistWriteBatchAsync(batch);
        }

        public Task UpsertLiveMessagesAsync(string chatJid, IReadOnlyList<ChatMessage> messages)
        {
            HistoryMessageWriteBatch batch = HistoryLiveMessageMapper.ToWriteBatch(chatJid, messages);
            return PersistWriteBatchAsync(batch);
        }

        public Task UpsertReactionsAsync(IReadOnlyList<HistoryMessageReaction> reactions)
        {
            var batch = new HistoryMessageWriteBatch();
            if (reactions != null)
            {
                for (int i = 0; i < reactions.Count; i++)
                {
                    if (reactions[i] != null)
                    {
                        batch.Reactions.Add(reactions[i]);
                    }
                }
            }

            return PersistWriteBatchAsync(batch);
        }

        public Task UpsertPinsAsync(IReadOnlyList<HistoryMessagePinUpdate> pins)
        {
            var batch = new HistoryMessageWriteBatch();
            if (pins != null)
            {
                for (int i = 0; i < pins.Count; i++)
                {
                    if (pins[i] != null)
                    {
                        batch.Pins.Add(pins[i]);
                    }
                }
            }

            return PersistWriteBatchAsync(batch);
        }

        public async Task<HistoryMessage> GetAsync(string chatJid, string messageId)
        {
            string key = JidHelper.Normalize(chatJid);
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(messageId))
            {
                return null;
            }

            await EnsureInitializedAsync().ConfigureAwait(false);
            HistoryMessageRow row = await _connection.FindAsync<HistoryMessageRow>(
                    HistoryMessageRow.MakeId(key, messageId.Trim()))
                .ConfigureAwait(false);
            HistoryMessage model = ToModel(row);
            if (model == null)
            {
                return null;
            }

            var list = new List<HistoryMessage> { model };
            await AttachReactionsSafeAsync(key, list).ConfigureAwait(false);
            return list[0];
        }

        public async Task PersistWriteBatchAsync(HistoryMessageWriteBatch batch)
        {
            if (batch == null || batch.IsEmpty)
            {
                return;
            }

            await EnsureInitializedAsync().ConfigureAwait(false);
            await _writeLock.WaitAsync().ConfigureAwait(false);
            string syncId = null;
            string syncType = null;
            int upserted = 0;
            var chats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                await _connection.RunInTransactionAsync(conn =>
                {
                    upserted += WriteMessages(conn, batch.Messages, chats, ref syncId, ref syncType);
                    if (batch.ReplaceExistingReactions)
                    {
                        ClearReactionsForMessages(conn, batch);
                    }

                    WriteReactions(conn, batch.Reactions, chats);
                    WritePins(conn, batch.Pins, chats);
                    WriteRevokes(conn, batch.Revokes, chats);
                }).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            if (upserted > 0 || chats.Count > 0)
            {
                Debug.WriteLine(
                    "[HistoryMessageStore] Persist msgs=" + upserted +
                    " reactions=" + (batch.Reactions?.Count ?? 0) +
                    " pins=" + (batch.Pins?.Count ?? 0) +
                    " revokes=" + (batch.Revokes?.Count ?? 0) +
                    " chats=" + chats.Count +
                    " syncId=" + (syncId ?? "") +
                    " type=" + (syncType ?? ""));
                var jids = new List<string>(chats);
                ChunkPersisted?.Invoke(this, new HistoryMessageChunkEventArgs
                {
                    SyncId = syncId ?? string.Empty,
                    SyncType = syncType ?? string.Empty,
                    UpsertedCount = upserted,
                    ConversationCount = chats.Count,
                    ChatJids = jids
                });
            }
        }

        public async Task<IReadOnlyList<HistoryMessage>> GetForChatAsync(
            string chatJid,
            int limit = 100,
            DateTime? beforeUtc = null,
            string beforeMessageId = null)
        {
            string key = JidHelper.Normalize(chatJid);
            if (string.IsNullOrWhiteSpace(key))
            {
                return Array.Empty<HistoryMessage>();
            }

            await EnsureInitializedAsync().ConfigureAwait(false);
            int take = Math.Max(1, limit);
            List<HistoryMessageRow> rows;
            if (beforeUtc.HasValue)
            {
                DateTime before = beforeUtc.Value;
                string beforeId = beforeMessageId ?? string.Empty;
                rows = await _connection.QueryAsync<HistoryMessageRow>(
                    TimelineSelectSql +
                    "WHERE ChatJid = ? AND (TimestampUtc < ? OR (TimestampUtc = ? AND MessageId < ?)) " +
                    "ORDER BY TimestampUtc DESC, MessageId DESC LIMIT ?",
                    key, before, before, beforeId, take).ConfigureAwait(false);
            }
            else
            {
                rows = await _connection.QueryAsync<HistoryMessageRow>(
                    TimelineSelectSql +
                    "WHERE ChatJid = ? ORDER BY TimestampUtc DESC, MessageId DESC LIMIT ?",
                    key, take).ConfigureAwait(false);
            }

            rows.Reverse();
            var list = new List<HistoryMessage>(rows.Count);
            foreach (var row in rows)
            {
                list.Add(ToModel(row));
            }

            await AttachReactionsSafeAsync(key, list).ConfigureAwait(false);
            return list;
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<HistoryMessage>> GetForChatKeysAsync(
            IReadOnlyList<string> chatJids,
            int limit = 100,
            DateTime? beforeUtc = null,
            string beforeMessageId = null)
        {
            var keys = NormalizeChatKeys(chatJids);
            if (keys.Count == 0)
            {
                return Array.Empty<HistoryMessage>();
            }

            if (keys.Count == 1)
            {
                return await GetForChatAsync(keys[0], limit, beforeUtc, beforeMessageId)
                    .ConfigureAwait(false);
            }

            await EnsureInitializedAsync().ConfigureAwait(false);
            int take = Math.Max(1, limit);

            var sql = new StringBuilder(TimelineSelectSql);
            sql.Append("WHERE ChatJid IN (");
            var args = new List<object>(keys.Count + 4);
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0)
                {
                    sql.Append(',');
                }

                sql.Append('?');
                args.Add(keys[i]);
            }

            sql.Append(')');

            if (beforeUtc.HasValue)
            {
                DateTime before = beforeUtc.Value;
                string beforeId = beforeMessageId ?? string.Empty;
                sql.Append(" AND (TimestampUtc < ? OR (TimestampUtc = ? AND MessageId < ?))");
                args.Add(before);
                args.Add(before);
                args.Add(beforeId);
            }

            sql.Append(" ORDER BY TimestampUtc DESC, MessageId DESC LIMIT ?");
            args.Add(take);

            List<HistoryMessageRow> rows =
                await _connection.QueryAsync<HistoryMessageRow>(sql.ToString(), args.ToArray())
                    .ConfigureAwait(false);

            rows.Reverse();

            var list = new List<HistoryMessage>(rows.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (row == null || string.IsNullOrWhiteSpace(row.MessageId))
                {
                    continue;
                }

                if (!seen.Add(row.MessageId))
                {
                    continue;
                }

                list.Add(ToModel(row));
            }

            await AttachReactionsForKeysSafeAsync(keys, list).ConfigureAwait(false);
            return list;
        }

        private static List<string> NormalizeChatKeys(IReadOnlyList<string> chatJids)
        {
            var keys = new List<string>();
            if (chatJids == null)
            {
                return keys;
            }

            for (int i = 0; i < chatJids.Count; i++)
            {
                string norm = JidHelper.Normalize(chatJids[i]);
                if (string.IsNullOrWhiteSpace(norm))
                {
                    continue;
                }

                bool exists = false;
                for (int j = 0; j < keys.Count; j++)
                {
                    if (string.Equals(keys[j], norm, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }

                if (!exists)
                {
                    keys.Add(norm);
                }
            }

            return keys;
        }

        private async Task AttachReactionsForKeysSafeAsync(
            IReadOnlyList<string> chatJids,
            List<HistoryMessage> messages)
        {
            try
            {
                await AttachReactionsAsync(chatJids, messages).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HistoryMessageStore] Reaction attach failed: " + ex.Message);
            }
        }

        private async Task AttachReactionsSafeAsync(string chatJid, List<HistoryMessage> messages)
        {
            try
            {
                await AttachReactionsAsync(new[] { chatJid }, messages).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HistoryMessageStore] Reaction attach failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Fills <see cref="HistoryMessage.Reactions"/> for a whole page: one batched query per 80
        /// ids, explicit columns, reactors ordered oldest first.
        /// </summary>
        private async Task AttachReactionsAsync(
            IReadOnlyList<string> chatJids,
            List<HistoryMessage> messages)
        {
            if (messages == null || messages.Count == 0 || chatJids == null || chatJids.Count == 0)
            {
                return;
            }

            var byId = new Dictionary<string, HistoryMessage>(StringComparer.Ordinal);
            var ids = new List<string>(messages.Count);
            CollectMessageIds(messages, byId, ids);
            if (ids.Count == 0)
            {
                return;
            }

            List<HistoryMessageReactionRow> rows =
                await QueryReactionRowsAsync(chatJids, ids).ConfigureAwait(false);
            if (rows == null || rows.Count == 0)
            {
                return;
            }

            for (int i = 0; i < rows.Count; i++)
            {
                HistoryMessageReactionRow row = rows[i];
                HistoryMessage parent;
                if (row == null ||
                    string.IsNullOrWhiteSpace(row.MessageId) ||
                    string.IsNullOrWhiteSpace(row.Emoji) ||
                    !byId.TryGetValue(row.MessageId, out parent) ||
                    parent == null)
                {
                    continue;
                }

                if (parent.Reactions == null)
                {
                    parent.Reactions = new List<HistoryMessageReaction>();
                }

                parent.Reactions.Add(ToReactionModel(row));
            }
        }

        private async Task<List<HistoryMessageReactionRow>> QueryReactionRowsAsync(
            IReadOnlyList<string> chatJids,
            List<string> messageIds)
        {
            const int batchSize = 80;
            var all = new List<HistoryMessageReactionRow>();
            if (chatJids == null || chatJids.Count == 0 || messageIds == null)
            {
                return all;
            }

            for (int offset = 0; offset < messageIds.Count; offset += batchSize)
            {
                int count = Math.Min(batchSize, messageIds.Count - offset);
                var sql = new StringBuilder(ReactionDetailSelectSql);
                sql.Append("WHERE ChatJid IN (");
                var args = new List<object>(chatJids.Count + count);
                for (int i = 0; i < chatJids.Count; i++)
                {
                    if (i > 0)
                    {
                        sql.Append(',');
                    }

                    sql.Append('?');
                    args.Add(chatJids[i]);
                }

                sql.Append(") AND MessageId IN (");
                for (int i = 0; i < count; i++)
                {
                    if (i > 0)
                    {
                        sql.Append(',');
                    }

                    sql.Append('?');
                    args.Add(messageIds[offset + i]);
                }

                sql.Append(") ORDER BY TimestampUtc ASC");
                List<HistoryMessageReactionRow> batch =
                    await _connection.QueryAsync<HistoryMessageReactionRow>(sql.ToString(), args.ToArray())
                        .ConfigureAwait(false);
                if (batch != null && batch.Count > 0)
                {
                    all.AddRange(batch);
                }
            }

            return all;
        }

        private static void CollectMessageIds(
            List<HistoryMessage> messages,
            Dictionary<string, HistoryMessage> byId,
            List<string> ids)
        {
            for (int i = 0; i < messages.Count; i++)
            {
                HistoryMessage message = messages[i];
                if (message == null || string.IsNullOrWhiteSpace(message.MessageId))
                {
                    continue;
                }

                if (byId.ContainsKey(message.MessageId))
                {
                    continue;
                }

                byId[message.MessageId] = message;
                ids.Add(message.MessageId);
            }
        }

        public async Task<IReadOnlyList<HistoryMessageReaction>> GetReactionsForMessageAsync(
            string chatJid,
            string messageId)
        {
            string key = JidHelper.Normalize(chatJid);
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(messageId))
            {
                return Array.Empty<HistoryMessageReaction>();
            }

            await EnsureInitializedAsync().ConfigureAwait(false);
            List<HistoryMessageReactionRow> rows = await _connection.QueryAsync<HistoryMessageReactionRow>(
                    ReactionDetailSelectSql +
                    "WHERE ChatJid = ? AND MessageId = ? ORDER BY TimestampUtc ASC",
                    key,
                    messageId.Trim())
                .ConfigureAwait(false);

            if (rows == null || rows.Count == 0)
            {
                return Array.Empty<HistoryMessageReaction>();
            }

            var list = new List<HistoryMessageReaction>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i] != null)
                {
                    list.Add(ToReactionModel(rows[i]));
                }
            }

            return list;
        }

        public async Task<IReadOnlyList<HistoryMessage>> GetPinnedForChatAsync(string chatJid, int maxCount = 3)
        {
            string key = JidHelper.Normalize(chatJid);
            if (string.IsNullOrWhiteSpace(key))
            {
                return Array.Empty<HistoryMessage>();
            }

            await EnsureInitializedAsync().ConfigureAwait(false);
            int take = Math.Max(1, maxCount);
            DateTime now = DateTime.UtcNow;
            List<HistoryMessageRow> rows = await _connection.QueryAsync<HistoryMessageRow>(
                    TimelineSelectSql +
                    "WHERE ChatJid = ? AND IsPinned = 1 " +
                    "ORDER BY PinnedAtUtc DESC LIMIT ?",
                    key, take * 3)
                .ConfigureAwait(false);

            var list = new List<HistoryMessage>();
            for (int i = 0; i < rows.Count && list.Count < take; i++)
            {
                HistoryMessageRow row = rows[i];
                if (row.PinExpiresAtUtc.HasValue && row.PinExpiresAtUtc.Value <= now)
                {
                    continue;
                }

                list.Add(ToModel(row));
            }

            await AttachReactionsSafeAsync(key, list).ConfigureAwait(false);
            return list;
        }

        public async Task<IReadOnlyList<HistoryMessage>> GetPendingOutgoingAsync(string chatJid)
        {
            string key = JidHelper.Normalize(chatJid);
            if (string.IsNullOrWhiteSpace(key))
            {
                return Array.Empty<HistoryMessage>();
            }

            await EnsureInitializedAsync().ConfigureAwait(false);
            int pending = (int)MessageSendState.Pending;
            int failed = (int)MessageSendState.Failed;
            List<HistoryMessageRow> rows = await _connection.Table<HistoryMessageRow>()
                .Where(r => r.ChatJid == key &&
                            r.IsFromMe &&
                            (r.SendState == pending || r.SendState == failed))
                .OrderBy(r => r.TimestampUtc)
                .ToListAsync()
                .ConfigureAwait(false);

            var list = new List<HistoryMessage>(rows.Count);
            foreach (var row in rows)
            {
                list.Add(ToModel(row));
            }

            await AttachReactionsSafeAsync(key, list).ConfigureAwait(false);
            return list;
        }

        public async Task<IReadOnlyList<HistoryMessage>> GetMediaForChatAsync(string chatJid, int limit = 400)
        {
            string key = JidHelper.Normalize(chatJid);
            if (string.IsNullOrWhiteSpace(key))
            {
                return Array.Empty<HistoryMessage>();
            }

            await EnsureInitializedAsync().ConfigureAwait(false);
            int take = Math.Max(1, limit);
            int image = (int)ChatPreviewKind.Image;
            int video = (int)ChatPreviewKind.Video;
            int voice = (int)ChatPreviewKind.Voice;
            int document = (int)ChatPreviewKind.Document;

            List<HistoryMessageRow> rows = await _connection.QueryAsync<HistoryMessageRow>(
                    TimelineSelectSql +
                    "WHERE ChatJid = ? AND Kind IN (?, ?, ?, ?) " +
                    "ORDER BY TimestampUtc DESC LIMIT ?",
                    key, image, video, voice, document, take)
                .ConfigureAwait(false);

            var list = new List<HistoryMessage>(rows.Count);
            foreach (var row in rows)
            {
                list.Add(ToModel(row));
            }

            return list;
        }

        public async Task<IReadOnlyList<HistoryMessage>> GetNewestPerChatAsync(IReadOnlyList<string> chatJids = null)
        {
            await EnsureInitializedAsync().ConfigureAwait(false);

            var keys = NormalizeChatKeys(chatJids);
            if (keys.Count == 0)
            {
                return Array.Empty<HistoryMessage>();
            }

            // One indexed LIMIT 1 per chat — reliable on older Mobile SQLite.
            var list = new List<HistoryMessage>(keys.Count);
            for (int i = 0; i < keys.Count; i++)
            {
                List<HistoryMessageRow> rows = await _connection.QueryAsync<HistoryMessageRow>(
                        TimelineSelectSql +
                        "WHERE ChatJid = ? AND IsRevoked = 0 AND TimestampUtc IS NOT NULL " +
                        "ORDER BY TimestampUtc DESC, MessageId DESC LIMIT 1",
                        keys[i])
                    .ConfigureAwait(false);
                if (rows == null || rows.Count == 0)
                {
                    continue;
                }

                HistoryMessage model = ToModel(rows[0]);
                if (model != null)
                {
                    list.Add(model);
                }
            }

            return list;
        }

        public async Task<int> CountAsync(string syncId = null)
        {
            await EnsureInitializedAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(syncId))
            {
                return await _connection.Table<HistoryMessageRow>().CountAsync().ConfigureAwait(false);
            }

            return await _connection.Table<HistoryMessageRow>()
                .Where(r => r.SyncId == syncId)
                .CountAsync()
                .ConfigureAwait(false);
        }

        public async Task ClearAsync(string reason = null)
        {
            await EnsureInitializedAsync().ConfigureAwait(false);
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _connection.DeleteAllAsync<HistoryMessageRow>().ConfigureAwait(false);
                await _connection.DeleteAllAsync<HistoryMessageReactionRow>().ConfigureAwait(false);
                Debug.WriteLine("[HistoryMessageStore] Cleared reason=" + (reason ?? ""));
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <inheritdoc />
        public async Task<int> DeleteForChatKeysAsync(IReadOnlyList<string> chatJids)
        {
            var keys = NormalizeChatKeys(chatJids);
            if (keys.Count == 0)
            {
                return 0;
            }

            await EnsureInitializedAsync().ConfigureAwait(false);
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var placeholders = new StringBuilder();
                var args = new object[keys.Count];
                for (int i = 0; i < keys.Count; i++)
                {
                    if (i > 0)
                    {
                        placeholders.Append(',');
                    }

                    placeholders.Append('?');
                    args[i] = keys[i];
                }

                string filter = " WHERE ChatJid IN (" + placeholders + ")";
                int removed = await _connection
                    .ExecuteAsync("DELETE FROM history_message" + filter, args)
                    .ConfigureAwait(false);
                await _connection
                    .ExecuteAsync("DELETE FROM history_message_reaction" + filter, args)
                    .ConfigureAwait(false);

                Debug.WriteLine(
                    "[HistoryMessageStore] Deleted " + removed + " row(s) for " + keys.Count + " key(s)");

                return removed;
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task EnsureInitializedAsync()
        {
            if (!_initialized)
            {
                await InitializeAsync().ConfigureAwait(false);
            }
        }

        private async Task DropLegacyThumbnailColumnAsync()
        {
            try
            {
                List<SqliteTableInfoRow> cols = await _connection
                    .QueryAsync<SqliteTableInfoRow>("PRAGMA table_info(history_message)")
                    .ConfigureAwait(false);
                bool hasColumn = false;
                if (cols != null)
                {
                    for (int i = 0; i < cols.Count; i++)
                    {
                        if (cols[i] != null &&
                            string.Equals(cols[i].name, "MediaThumbnailBase64", StringComparison.OrdinalIgnoreCase))
                        {
                            hasColumn = true;
                            break;
                        }
                    }
                }

                if (!hasColumn)
                {
                    return;
                }

                await _connection.ExecuteAsync(
                        "ALTER TABLE history_message DROP COLUMN MediaThumbnailBase64")
                    .ConfigureAwait(false);
                Debug.WriteLine("[HistoryMessageStore] Dropped MediaThumbnailBase64 column");
            }
            catch (Exception ex)
            {
                // Older SQLite without DROP COLUMN: property is gone from the entity; column may linger.
                Debug.WriteLine("[HistoryMessageStore] Drop MediaThumbnailBase64 skipped: " + ex.Message);
            }
        }

        private sealed class SqliteTableInfoRow
        {
            public int cid { get; set; }

            public string name { get; set; }

            public string type { get; set; }
        }

        private async Task EnsureIndexesAsync()
        {
            try
            {
                await _connection.ExecuteAsync(
                        "CREATE INDEX IF NOT EXISTS ix_hm_chat_ts ON history_message (ChatJid, TimestampUtc, MessageId)")
                    .ConfigureAwait(false);
                await _connection.ExecuteAsync(
                        "CREATE INDEX IF NOT EXISTS ix_hm_chat_pin ON history_message (ChatJid, IsPinned, PinnedAtUtc)")
                    .ConfigureAwait(false);
                await _connection.ExecuteAsync(
                        "CREATE INDEX IF NOT EXISTS ix_hm_chat_kind ON history_message (ChatJid, Kind, TimestampUtc)")
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HistoryMessageStore] Indexes: " + ex.Message);
            }
        }

        private static bool IsThumbCacheUri(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
            {
                return false;
            }

            return uri.IndexOf("_thumb.", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   uri.EndsWith("_thumb", StringComparison.OrdinalIgnoreCase);
        }

        private static int WriteMessages(
            SQLiteConnection conn,
            List<HistoryMessage> rows,
            HashSet<string> chats,
            ref string syncId,
            ref string syncType)
        {
            int upserted = 0;
            if (rows == null)
            {
                return 0;
            }

            for (int i = 0; i < rows.Count; i++)
            {
                HistoryMessage model = rows[i];
                if (model == null ||
                    string.IsNullOrWhiteSpace(model.ChatJid) ||
                    string.IsNullOrWhiteSpace(model.MessageId))
                {
                    continue;
                }

                model.ChatJid = JidHelper.Normalize(model.ChatJid);
                syncId = model.SyncId ?? syncId;
                syncType = model.SyncType ?? syncType;
                chats.Add(model.ChatJid);
                HistoryMessageRow row = ToRow(model);
                HistoryMessageRow existing = conn.Find<HistoryMessageRow>(row.Id);
                if (existing != null)
                {
                    if (string.IsNullOrWhiteSpace(row.MediaLocalUri))
                    {
                        row.MediaLocalUri = existing.MediaLocalUri;
                    }
                    else if (IsThumbCacheUri(row.MediaLocalUri) &&
                             !string.IsNullOrWhiteSpace(existing.MediaLocalUri) &&
                             !IsThumbCacheUri(existing.MediaLocalUri))
                    {
                        // Keep downloaded full media; history thumb must not clobber it.
                        row.MediaLocalUri = existing.MediaLocalUri;
                    }

                    if (string.IsNullOrWhiteSpace(row.MediaPosterUri))
                    {
                        row.MediaPosterUri = existing.MediaPosterUri;
                    }
                    else if (IsThumbCacheUri(row.MediaPosterUri) &&
                             !string.IsNullOrWhiteSpace(existing.MediaPosterUri) &&
                             !IsThumbCacheUri(existing.MediaPosterUri))
                    {
                        row.MediaPosterUri = existing.MediaPosterUri;
                    }

                    if (existing.IsRevoked)
                    {
                        row.IsRevoked = true;
                    }

                    // Pin/unpin is authoritative only via WritePins (or an upsert that already
                    // carries IsPinned=true). History/live body rewrites must not clear a pin
                    // the strip/banner already persisted — same idea as IsRevoked.
                    if (existing.IsPinned && !row.IsPinned)
                    {
                        row.IsPinned = true;
                        row.PinnedAtUtc = existing.PinnedAtUtc;
                        row.PinExpiresAtUtc = existing.PinExpiresAtUtc;
                    }
                }

                // Never persist fat protocol thumbnails in SQLite (disk URI is enough).
                conn.InsertOrReplace(row);
                upserted++;
            }

            return upserted;
        }

        /// <summary>
        /// Only the ids the batch is authoritative about: chip-summary rows carry no reactor rows,
        /// so clearing by message would drop stored reactions on every live upsert.
        /// </summary>
        private static void ClearReactionsForMessages(SQLiteConnection conn, HistoryMessageWriteBatch batch)
        {
            if (conn == null || batch == null || batch.ReactionOwnerMessageIds.Count == 0)
            {
                return;
            }

            var owners = new HashSet<string>(batch.ReactionOwnerMessageIds, StringComparer.Ordinal);
            List<HistoryMessage> messages = batch.Messages;
            for (int i = 0; i < messages.Count; i++)
            {
                HistoryMessage message = messages[i];
                if (message == null ||
                    string.IsNullOrWhiteSpace(message.ChatJid) ||
                    string.IsNullOrWhiteSpace(message.MessageId) ||
                    !owners.Contains(message.MessageId))
                {
                    continue;
                }

                conn.Execute(
                    "DELETE FROM history_message_reaction WHERE ChatJid = ? AND MessageId = ?",
                    message.ChatJid,
                    message.MessageId);
            }
        }

        private static void WriteReactions(
            SQLiteConnection conn,
            List<HistoryMessageReaction> rows,
            HashSet<string> chats)
        {
            if (rows == null)
            {
                return;
            }

            for (int i = 0; i < rows.Count; i++)
            {
                HistoryMessageReaction model = rows[i];
                if (model == null ||
                    string.IsNullOrWhiteSpace(model.ChatJid) ||
                    string.IsNullOrWhiteSpace(model.MessageId) ||
                    string.IsNullOrWhiteSpace(model.ReactorJid))
                {
                    continue;
                }

                string chatJid = JidHelper.Normalize(model.ChatJid);
                string reactorJid = JidHelper.Normalize(model.ReactorJid);
                if (string.IsNullOrWhiteSpace(reactorJid))
                {
                    reactorJid = model.ReactorJid.Trim();
                }

                string id = HistoryMessageReactionRow.MakeId(chatJid, model.MessageId.Trim(), reactorJid);
                chats.Add(chatJid);

                if (string.IsNullOrWhiteSpace(model.Emoji))
                {
                    conn.Delete<HistoryMessageReactionRow>(id);
                    continue;
                }

                conn.InsertOrReplace(new HistoryMessageReactionRow
                {
                    Id = id,
                    ChatJid = chatJid,
                    MessageId = model.MessageId.Trim(),
                    ReactorJid = reactorJid,
                    ReactorName = model.ReactorName,
                    Emoji = model.Emoji.Trim(),
                    FromMe = model.FromMe,
                    ReactionMessageId = model.ReactionMessageId,
                    TimestampUtc = model.TimestampUtc == default ? DateTime.UtcNow : model.TimestampUtc
                });
            }
        }

        private static void WritePins(
            SQLiteConnection conn,
            List<HistoryMessagePinUpdate> pins,
            HashSet<string> chats)
        {
            if (pins == null)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            for (int i = 0; i < pins.Count; i++)
            {
                HistoryMessagePinUpdate pin = pins[i];
                if (pin == null ||
                    string.IsNullOrWhiteSpace(pin.ChatJid) ||
                    string.IsNullOrWhiteSpace(pin.MessageId))
                {
                    continue;
                }

                string chatJid = JidHelper.Normalize(pin.ChatJid);
                string messageId = pin.MessageId.Trim();
                string id = HistoryMessageRow.MakeId(chatJid, messageId);
                var row = conn.Find<HistoryMessageRow>(id);
                if (row == null)
                {
                    // History may have stored the body under PN while the pin envelope used LID
                    // (or the reverse). MessageId is unique enough for a single-chat fallback.
                    row = conn.FindWithQuery<HistoryMessageRow>(
                        "SELECT * FROM history_message WHERE MessageId = ? LIMIT 1",
                        messageId);
                    if (row == null)
                    {
                        continue;
                    }
                }

                row.IsPinned = pin.IsPinned;
                row.PinnedAtUtc = pin.IsPinned ? pin.PinnedAtUtc : null;
                row.PinExpiresAtUtc = pin.IsPinned ? pin.PinExpiresAtUtc : null;
                row.UpdatedAtUtc = now;
                conn.Update(row);
                chats.Add(row.ChatJid ?? chatJid);
            }
        }

        private static void WriteRevokes(
            SQLiteConnection conn,
            List<HistoryMessageRevoke> revokes,
            HashSet<string> chats)
        {
            if (revokes == null)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            for (int i = 0; i < revokes.Count; i++)
            {
                HistoryMessageRevoke revoke = revokes[i];
                if (revoke == null ||
                    string.IsNullOrWhiteSpace(revoke.ChatJid) ||
                    string.IsNullOrWhiteSpace(revoke.MessageId))
                {
                    continue;
                }

                string chatJid = JidHelper.Normalize(revoke.ChatJid);
                string id = HistoryMessageRow.MakeId(chatJid, revoke.MessageId.Trim());
                var row = conn.Find<HistoryMessageRow>(id);
                if (row == null)
                {
                    continue;
                }

                row.IsRevoked = true;
                row.UpdatedAtUtc = now;
                conn.Update(row);
                chats.Add(chatJid);
            }
        }

        private static HistoryMessageRow ToRow(HistoryMessage model)
        {
            return new HistoryMessageRow
            {
                Id = HistoryMessageRow.MakeId(model.ChatJid, model.MessageId),
                ChatJid = model.ChatJid,
                MessageId = model.MessageId,
                IsFromMe = model.IsFromMe,
                ParticipantJid = model.ParticipantJid,
                SenderName = model.SenderName,
                Body = model.Body,
                Kind = (int)model.Kind,
                SendState = (int)model.SendState,
                MediaUrl = model.MediaUrl,
                MediaDirectPath = model.MediaDirectPath,
                MediaKeyBase64 = model.MediaKeyBase64,
                MediaFileEncSha256Base64 = model.MediaFileEncSha256Base64,
                MediaMimeType = model.MediaMimeType,
                MediaDurationSeconds = model.MediaDurationSeconds,
                MediaFileName = model.MediaFileName,
                MediaFileLengthBytes = model.MediaFileLengthBytes,
                IsVoiceNote = model.IsVoiceNote,
                IsRevoked = model.IsRevoked,
                IsForwarded = model.IsForwarded,
                IsPinned = model.IsPinned,
                PinnedAtUtc = model.PinnedAtUtc,
                PinExpiresAtUtc = model.PinExpiresAtUtc,
                QuotedMessageId = model.QuotedMessageId,
                QuotedChatJid = model.QuotedChatJid,
                QuotedParticipantJid = model.QuotedParticipantJid,
                QuotedSenderName = model.QuotedSenderName,
                QuotedBody = model.QuotedBody,
                QuotedKind = (int)model.QuotedKind,
                MediaLocalUri = model.MediaLocalUri,
                MediaPosterUri = model.MediaPosterUri,
                MentionedJids = JoinMentionedJids(model.MentionedJids),
                TimestampUtc = model.TimestampUtc,
                SyncId = model.SyncId ?? string.Empty,
                SyncType = model.SyncType ?? string.Empty,
                UpdatedAtUtc = model.UpdatedAtUtc == default ? DateTime.UtcNow : model.UpdatedAtUtc
            };
        }

        private static HistoryMessage ToModel(HistoryMessageRow row)
        {
            if (row == null)
            {
                return null;
            }

            ChatPreviewKind kind = ChatPreviewKind.Text;
            if (row.Kind >= (int)ChatPreviewKind.Text && row.Kind <= (int)ChatPreviewKind.Reaction)
            {
                kind = (ChatPreviewKind)row.Kind;
            }

            ChatPreviewKind quotedKind = ChatPreviewKind.Text;
            if (row.QuotedKind >= (int)ChatPreviewKind.Text && row.QuotedKind <= (int)ChatPreviewKind.Reaction)
            {
                quotedKind = (ChatPreviewKind)row.QuotedKind;
            }

            MessageSendState sendState = MessageSendState.NotApplicable;
            if (row.SendState >= (int)MessageSendState.NotApplicable &&
                row.SendState <= (int)MessageSendState.Failed)
            {
                sendState = (MessageSendState)row.SendState;
            }

            return new HistoryMessage
            {
                ChatJid = row.ChatJid,
                MessageId = row.MessageId,
                IsFromMe = row.IsFromMe,
                ParticipantJid = row.ParticipantJid,
                SenderName = row.SenderName,
                Body = row.Body,
                Kind = kind,
                SendState = sendState,
                MediaUrl = row.MediaUrl,
                MediaDirectPath = row.MediaDirectPath,
                MediaKeyBase64 = row.MediaKeyBase64,
                MediaFileEncSha256Base64 = row.MediaFileEncSha256Base64,
                MediaMimeType = row.MediaMimeType,
                MediaDurationSeconds = row.MediaDurationSeconds,
                MediaFileName = row.MediaFileName,
                MediaFileLengthBytes = row.MediaFileLengthBytes,
                IsVoiceNote = row.IsVoiceNote,
                IsRevoked = row.IsRevoked,
                IsForwarded = row.IsForwarded,
                IsPinned = row.IsPinned,
                PinnedAtUtc = row.PinnedAtUtc,
                PinExpiresAtUtc = row.PinExpiresAtUtc,
                QuotedMessageId = row.QuotedMessageId,
                QuotedChatJid = row.QuotedChatJid,
                QuotedParticipantJid = row.QuotedParticipantJid,
                QuotedSenderName = row.QuotedSenderName,
                QuotedBody = row.QuotedBody,
                QuotedKind = quotedKind,
                MediaLocalUri = row.MediaLocalUri,
                MediaPosterUri = row.MediaPosterUri,
                MentionedJids = SplitMentionedJids(row.MentionedJids),
                TimestampUtc = row.TimestampUtc,
                SyncId = row.SyncId,
                SyncType = row.SyncType,
                UpdatedAtUtc = row.UpdatedAtUtc
            };
        }

        private static string JoinMentionedJids(IReadOnlyList<string> jids)
        {
            if (jids == null || jids.Count == 0)
            {
                return null;
            }

            var sb = new StringBuilder();
            for (int i = 0; i < jids.Count; i++)
            {
                string jid = jids[i];
                if (string.IsNullOrWhiteSpace(jid))
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.Append(',');
                }

                sb.Append(jid.Trim());
            }

            return sb.Length == 0 ? null : sb.ToString();
        }

        private static List<string> SplitMentionedJids(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            string[] parts = raw.Split(',');
            var list = new List<string>(parts.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                string jid = parts[i] != null ? parts[i].Trim() : null;
                if (!string.IsNullOrEmpty(jid))
                {
                    list.Add(jid);
                }
            }

            return list.Count == 0 ? null : list;
        }

        private static HistoryMessageReaction ToReactionModel(HistoryMessageReactionRow row)
        {
            return new HistoryMessageReaction
            {
                ChatJid = row.ChatJid,
                MessageId = row.MessageId,
                ReactorJid = row.ReactorJid,
                ReactorName = row.ReactorName,
                Emoji = row.Emoji,
                FromMe = row.FromMe,
                ReactionMessageId = row.ReactionMessageId,
                TimestampUtc = row.TimestampUtc
            };
        }
    }
}
