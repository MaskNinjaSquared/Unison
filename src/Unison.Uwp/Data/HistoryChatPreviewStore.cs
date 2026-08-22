using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SQLite;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.Uwp.Data.Entities;
using Windows.Storage;

namespace Unison.Uwp.Data
{
    /// <summary>
    /// SQLite <c>history_chat_preview</c> — list rows from history chunks (same <c>unison.db</c>).
    /// </summary>
    public sealed class HistoryChatPreviewStore : IHistoryChatPreviewStore
    {
        private static readonly string DatabaseFileName = "unison.db";

        public const int CurrentSchemaVersion = 4;

        private readonly SemaphoreSlim _initLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        private SQLiteAsyncConnection _connection;
        private bool _initialized;

        public int SchemaVersion => CurrentSchemaVersion;

        public event EventHandler<HistoryChatPreviewChunkEventArgs> ChunkPersisted;

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
                await _connection.CreateTableAsync<HistoryChatPreviewRow>().ConfigureAwait(false);
                await EnsureLastMessageIdColumnAsync().ConfigureAwait(false);
                _initialized = true;
                Debug.WriteLine("[HistoryChatPreviewStore] Initialized at " + dbPath);
            }
            finally
            {
                _initLock.Release();
            }
        }

        public async Task UpsertManyAsync(IReadOnlyList<HistoryChatPreview> rows, bool notifyChunk = true)
        {
            if (rows == null || rows.Count == 0)
            {
                return;
            }

            await EnsureInitializedAsync().ConfigureAwait(false);
            await _writeLock.WaitAsync().ConfigureAwait(false);
            string syncId = null;
            string syncType = null;
            int upserted = 0;
            try
            {
                await _connection.RunInTransactionAsync(conn =>
                {
                    foreach (var model in rows)
                    {
                        if (model == null || string.IsNullOrWhiteSpace(model.Jid))
                        {
                            continue;
                        }

                        syncId = model.SyncId ?? syncId;
                        syncType = model.SyncType ?? syncType;
                        conn.InsertOrReplace(ToRow(model));
                        upserted++;
                    }
                }).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            if (upserted > 0)
            {
                Debug.WriteLine(
                    "[HistoryChatPreviewStore] Upserted " + upserted +
                    " syncId=" + (syncId ?? "") +
                    " type=" + (syncType ?? "") +
                    " notify=" + notifyChunk);
                if (notifyChunk)
                {
                    ChunkPersisted?.Invoke(this, new HistoryChatPreviewChunkEventArgs
                    {
                        SyncId = syncId ?? string.Empty,
                        SyncType = syncType ?? string.Empty,
                        UpsertedCount = upserted,
                        Rows = rows
                    });
                }
            }
        }

        public async Task<IReadOnlyList<HistoryChatPreview>> GetAllAsync(string syncId = null)
        {
            await EnsureInitializedAsync().ConfigureAwait(false);
            List<HistoryChatPreviewRow> rows;
            if (string.IsNullOrWhiteSpace(syncId))
            {
                rows = await _connection.Table<HistoryChatPreviewRow>()
                    .OrderByDescending(r => r.LastMessageTimestampUtc)
                    .ToListAsync()
                    .ConfigureAwait(false);
            }
            else
            {
                rows = await _connection.Table<HistoryChatPreviewRow>()
                    .Where(r => r.SyncId == syncId)
                    .OrderByDescending(r => r.LastMessageTimestampUtc)
                    .ToListAsync()
                    .ConfigureAwait(false);
            }

            var list = new List<HistoryChatPreview>(rows.Count);
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
                return await _connection.Table<HistoryChatPreviewRow>().CountAsync().ConfigureAwait(false);
            }

            return await _connection.Table<HistoryChatPreviewRow>()
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
                await _connection.DeleteAllAsync<HistoryChatPreviewRow>().ConfigureAwait(false);
                Debug.WriteLine("[HistoryChatPreviewStore] Cleared reason=" + (reason ?? ""));
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

        private async Task EnsureLastMessageIdColumnAsync()
        {
            try
            {
                List<SqliteTableInfoRow> cols = await _connection
                    .QueryAsync<SqliteTableInfoRow>("PRAGMA table_info(history_chat_preview)")
                    .ConfigureAwait(false);
                bool hasId = false;
                if (cols != null)
                {
                    for (int i = 0; i < cols.Count; i++)
                    {
                        if (string.Equals(cols[i]?.name, "LastMessageId", StringComparison.OrdinalIgnoreCase))
                        {
                            hasId = true;
                            break;
                        }
                    }
                }

                if (!hasId)
                {
                    await _connection.ExecuteAsync(
                            "ALTER TABLE history_chat_preview ADD COLUMN LastMessageId TEXT")
                        .ConfigureAwait(false);
                    Debug.WriteLine("[HistoryChatPreviewStore] Added LastMessageId column");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HistoryChatPreviewStore] EnsureLastMessageIdColumn failed: " + ex.Message);
            }
        }

        private sealed class SqliteTableInfoRow
        {
            public int cid { get; set; }
            public string name { get; set; }
            public string type { get; set; }
        }

        private static HistoryChatPreviewRow ToRow(HistoryChatPreview model)
        {
            return new HistoryChatPreviewRow
            {
                Jid = model.Jid,
                LidJid = model.LidJid,
                PnJid = model.PnJid,
                Name = model.Name,
                IsGroup = model.IsGroup,
                UnreadCount = Math.Max(0, model.UnreadCount),
                LastMessage = model.LastMessage,
                LastMessageAuthor = model.LastMessageAuthor,
                LastMessageIsFromMe = model.LastMessageIsFromMe,
                LastMessageSenderName = model.LastMessageSenderName,
                LastMessageParticipantJid = model.LastMessageParticipantJid,
                LastMessageKind = (int)model.LastMessageKind,
                LastMessageSendState = (int)model.LastMessageSendState,
                LastMessageMentionedJids = JoinMentionedJids(model.LastMessageMentionedJids),
                LastMessageId = model.LastMessageId,
                LastMessageTimestampUtc = model.LastMessageTimestampUtc,
                SyncId = model.SyncId ?? string.Empty,
                SyncType = model.SyncType ?? string.Empty,
                UpdatedAtUtc = model.UpdatedAtUtc == default ? DateTime.UtcNow : model.UpdatedAtUtc
            };
        }

        private static HistoryChatPreview ToModel(HistoryChatPreviewRow row)
        {
            if (row == null)
            {
                return null;
            }

            ChatPreviewKind kind = ChatPreviewKind.Text;
            if (row.LastMessageKind >= (int)ChatPreviewKind.Text &&
                row.LastMessageKind <= (int)ChatPreviewKind.Reaction)
            {
                kind = (ChatPreviewKind)row.LastMessageKind;
            }

            MessageSendState sendState = MessageSendState.NotApplicable;
            if (row.LastMessageSendState >= (int)MessageSendState.NotApplicable &&
                row.LastMessageSendState <= (int)MessageSendState.Failed)
            {
                sendState = (MessageSendState)row.LastMessageSendState;
            }

            if (!row.LastMessageIsFromMe)
            {
                sendState = MessageSendState.NotApplicable;
            }

            return new HistoryChatPreview
            {
                Jid = row.Jid,
                LidJid = row.LidJid,
                PnJid = row.PnJid,
                Name = row.Name,
                IsGroup = row.IsGroup,
                UnreadCount = row.UnreadCount,
                LastMessage = row.LastMessage,
                LastMessageAuthor = row.LastMessageAuthor,
                LastMessageIsFromMe = row.LastMessageIsFromMe,
                LastMessageSenderName = row.LastMessageSenderName,
                LastMessageParticipantJid = row.LastMessageParticipantJid,
                LastMessageKind = kind,
                LastMessageSendState = sendState,
                LastMessageMentionedJids = SplitMentionedJids(row.LastMessageMentionedJids),
                LastMessageId = row.LastMessageId,
                LastMessageTimestampUtc = row.LastMessageTimestampUtc,
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
    }
}
