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

        public const int CurrentSchemaVersion = 5;

        /// <summary>
        /// Timeline open / load-more omit <c>MediaThumbnailBase64</c>; media kinds fill it in a second query.
        /// </summary>
        private const string TimelineSelectSql =
            "SELECT Id, ChatJid, MessageId, IsFromMe, ParticipantJid, SenderName, Body, Kind, SendState, " +
            "MediaUrl, MediaDirectPath, MediaKeyBase64, MediaFileEncSha256Base64, MediaMimeType, " +
            "MediaDurationSeconds, MediaFileName, MediaFileLengthBytes, IsVoiceNote, IsRevoked, IsPinned, " +
            "PinnedAtUtc, PinExpiresAtUtc, QuotedMessageId, QuotedChatJid, QuotedParticipantJid, " +
            "QuotedSenderName, QuotedBody, QuotedKind, MediaLocalUri, MediaPosterUri, MentionedJids, " +
            "TimestampUtc, SyncId, SyncType, UpdatedAtUtc FROM history_message ";

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
            await AttachReactionsAsync(key, list).ConfigureAwait(false);
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
                        ClearReactionsForMessages(conn, batch.Messages);
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

            await FillTimelineThumbnailsAsync(rows).ConfigureAwait(false);
            rows.Reverse();
            var list = new List<HistoryMessage>(rows.Count);
            foreach (var row in rows)
            {
                list.Add(ToModel(row));
            }

            await AttachReactionsAsync(key, list).ConfigureAwait(false);
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
                    "SELECT * FROM history_message WHERE ChatJid = ? AND IsPinned = 1 " +
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

            await AttachReactionsAsync(key, list).ConfigureAwait(false);
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

            await AttachReactionsAsync(key, list).ConfigureAwait(false);
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
                    "SELECT * FROM history_message WHERE ChatJid = ? AND Kind IN (?, ?, ?, ?) " +
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

        private async Task AttachReactionsAsync(string chatJid, List<HistoryMessage> messages)
        {
            if (messages == null || messages.Count == 0)
            {
                return;
            }

            var byId = new Dictionary<string, HistoryMessage>(StringComparer.Ordinal);
            var ids = new List<string>(messages.Count);
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

            if (ids.Count == 0)
            {
                return;
            }

            List<HistoryMessageReactionRow> rows = await QueryReactionsAsync(chatJid, ids).ConfigureAwait(false);
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

        private async Task<List<HistoryMessageReactionRow>> QueryReactionsAsync(
            string chatJid,
            List<string> messageIds)
        {
            const int batchSize = 80;
            var all = new List<HistoryMessageReactionRow>();
            for (int offset = 0; offset < messageIds.Count; offset += batchSize)
            {
                int count = Math.Min(batchSize, messageIds.Count - offset);
                var sql = new StringBuilder(
                    "SELECT * FROM history_message_reaction WHERE ChatJid = ? AND MessageId IN (");
                var args = new object[count + 1];
                args[0] = chatJid;
                for (int i = 0; i < count; i++)
                {
                    if (i > 0)
                    {
                        sql.Append(',');
                    }

                    sql.Append('?');
                    args[i + 1] = messageIds[offset + i];
                }

                sql.Append(')');
                List<HistoryMessageReactionRow> chunk =
                    await _connection.QueryAsync<HistoryMessageReactionRow>(sql.ToString(), args)
                        .ConfigureAwait(false);
                if (chunk != null && chunk.Count > 0)
                {
                    all.AddRange(chunk);
                }
            }

            return all;
        }

        private async Task EnsureInitializedAsync()
        {
            if (!_initialized)
            {
                await InitializeAsync().ConfigureAwait(false);
            }
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

        private async Task FillTimelineThumbnailsAsync(List<HistoryMessageRow> rows)
        {
            if (rows == null || rows.Count == 0)
            {
                return;
            }

            var ids = new List<string>();
            for (int i = 0; i < rows.Count; i++)
            {
                HistoryMessageRow row = rows[i];
                if (row != null && NeedsTimelineThumbnail(row.Kind) && !string.IsNullOrEmpty(row.Id))
                {
                    ids.Add(row.Id);
                }
            }

            if (ids.Count == 0)
            {
                return;
            }

            var byId = new Dictionary<string, HistoryMessageRow>(rows.Count, StringComparer.Ordinal);
            for (int i = 0; i < rows.Count; i++)
            {
                HistoryMessageRow row = rows[i];
                if (row != null && !string.IsNullOrEmpty(row.Id))
                {
                    byId[row.Id] = row;
                }
            }

            const int batchSize = 80;
            for (int offset = 0; offset < ids.Count; offset += batchSize)
            {
                int count = Math.Min(batchSize, ids.Count - offset);
                var sql = new StringBuilder(
                    "SELECT Id, MediaThumbnailBase64 FROM history_message WHERE Id IN (");
                var args = new object[count];
                for (int i = 0; i < count; i++)
                {
                    if (i > 0)
                    {
                        sql.Append(',');
                    }

                    sql.Append('?');
                    args[i] = ids[offset + i];
                }

                sql.Append(')');
                List<HistoryMessageRow> thumbs =
                    await _connection.QueryAsync<HistoryMessageRow>(sql.ToString(), args)
                        .ConfigureAwait(false);
                if (thumbs == null)
                {
                    continue;
                }

                for (int i = 0; i < thumbs.Count; i++)
                {
                    HistoryMessageRow thumb = thumbs[i];
                    HistoryMessageRow parent;
                    if (thumb == null ||
                        string.IsNullOrEmpty(thumb.Id) ||
                        !byId.TryGetValue(thumb.Id, out parent) ||
                        parent == null)
                    {
                        continue;
                    }

                    parent.MediaThumbnailBase64 = thumb.MediaThumbnailBase64;
                }
            }
        }

        private static bool NeedsTimelineThumbnail(int kind)
        {
            return kind == (int)ChatPreviewKind.Image ||
                   kind == (int)ChatPreviewKind.Video ||
                   kind == (int)ChatPreviewKind.Sticker ||
                   kind == (int)ChatPreviewKind.Document;
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

                    if (string.IsNullOrWhiteSpace(row.MediaPosterUri))
                    {
                        row.MediaPosterUri = existing.MediaPosterUri;
                    }

                    if (existing.IsRevoked)
                    {
                        row.IsRevoked = true;
                    }
                }

                conn.InsertOrReplace(row);
                upserted++;
            }

            return upserted;
        }

        private static void ClearReactionsForMessages(SQLiteConnection conn, List<HistoryMessage> messages)
        {
            if (conn == null || messages == null)
            {
                return;
            }

            for (int i = 0; i < messages.Count; i++)
            {
                HistoryMessage message = messages[i];
                if (message == null ||
                    string.IsNullOrWhiteSpace(message.ChatJid) ||
                    string.IsNullOrWhiteSpace(message.MessageId))
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
                string id = HistoryMessageRow.MakeId(chatJid, pin.MessageId.Trim());
                var row = conn.Find<HistoryMessageRow>(id);
                if (row == null)
                {
                    continue;
                }

                row.IsPinned = pin.IsPinned;
                row.PinnedAtUtc = pin.IsPinned ? pin.PinnedAtUtc : null;
                row.PinExpiresAtUtc = pin.IsPinned ? pin.PinExpiresAtUtc : null;
                row.UpdatedAtUtc = now;
                conn.Update(row);
                chats.Add(chatJid);
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
                MediaThumbnailBase64 = model.MediaThumbnailBase64,
                IsVoiceNote = model.IsVoiceNote,
                IsRevoked = model.IsRevoked,
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
                MediaThumbnailBase64 = row.MediaThumbnailBase64,
                IsVoiceNote = row.IsVoiceNote,
                IsRevoked = row.IsRevoked,
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
