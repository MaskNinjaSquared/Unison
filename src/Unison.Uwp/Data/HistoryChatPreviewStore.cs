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

        public const int CurrentSchemaVersion = 6;

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
                await EnsureColumnAsync("LastMessageId", "TEXT").ConfigureAwait(false);
                await EnsureColumnAsync("DeletedAtUtc", "DATETIME").ConfigureAwait(false);
                await EnsureColumnAsync("Status", "INTEGER NOT NULL DEFAULT 0").ConfigureAwait(false);
                await _connection.ExecuteAsync(
                        "UPDATE history_chat_preview SET Status = ? WHERE DeletedAtUtc IS NOT NULL",
                        (int)ChatStatus.Deleted)
                    .ConfigureAwait(false);
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
            int inserted = 0;
            int flagUpdates = 0;
            int tipUpdates = 0;
            int skipped = 0;
            var notified = new List<HistoryChatPreview>();
            try
            {
                // Read inside the write lock. The snapshot is what decides insert / update /
                // skip, so reading it outside let two concurrent writers judge against the same
                // stale picture and one of them conclude "nothing changed" about a row the other
                // had already moved.
                //
                // Light snapshot: enough to decide insert vs lifecycle/tip delta without loading
                // bodies, and scoped to the batch when the batch is small - a one-row slice used
                // to scan the whole catalogue twice, which is the cost this store exists to avoid.
                string[] scope = ScopeFor(rows);
                var existing = await LoadLifecycleSnapshotAsync(scope).ConfigureAwait(false);
                var tombstones = await LoadTombstonesAsync(scope).ConfigureAwait(false);

                await _connection.RunInTransactionAsync(conn =>
                {
                    foreach (var model in rows)
                    {
                        if (model == null || string.IsNullOrWhiteSpace(model.Jid))
                        {
                            continue;
                        }

                        string jid = model.Jid;
                        syncId = model.SyncId ?? syncId;
                        syncType = model.SyncType ?? syncType;
                        DateTime? carried = CarriedTombstone(tombstones, model);
                        ChatStatus status = ResolveStatus(tombstones, model, carried);

                        LifecycleSnapshot prior;
                        if (!existing.TryGetValue(jid, out prior))
                        {
                            conn.InsertOrReplace(ToRow(model, carried, status));
                            upserted++;
                            inserted++;
                            notified.Add(CloneForNotify(model, status));
                            existing[jid] = new LifecycleSnapshot
                            {
                                Jid = jid,
                                Status = (int)status,
                                DeletedAtUtc = carried,
                                LastMessageTimestampUtc = model.LastMessageTimestampUtc,
                                LastMessageId = model.LastMessageId,
                                UnreadCount = Math.Max(0, model.UnreadCount),
                                Name = model.Name,
                                LastMessageSendState = (int)model.LastMessageSendState,
                                IsGroup = model.IsGroup,
                                LidJid = model.LidJid,
                                PnJid = model.PnJid
                            };
                            continue;
                        }

                        bool tipNewer = IsIncomingTipNewer(prior, model);
                        bool statusChanged = prior.Status != (int)status ||
                                             !NullableDateEquals(prior.DeletedAtUtc, carried);
                        bool stripChanged = HasStripChange(prior, model);

                        if (!tipNewer && !statusChanged && !stripChanged)
                        {
                            // Preview table has no pin/mute columns. A chunk that only carries
                            // those still has to reach the list — otherwise a pinned conversation
                            // whose tip did not move never shows the icon.
                            if (HasLocalFlagPayload(model))
                            {
                                notified.Add(CloneForNotify(model, status));
                            }
                            else
                            {
                                skipped++;
                            }

                            continue;
                        }

                        if (tipNewer)
                        {
                            // Tip moved: rewrite the list strip (and lifecycle) in one pass.
                            conn.InsertOrReplace(ToRow(model, carried, status, prior));
                            tipUpdates++;
                            upserted++;
                            notified.Add(CloneForNotify(model, status));
                            prior.LastMessageTimestampUtc = model.LastMessageTimestampUtc;
                            prior.LastMessageId = model.LastMessageId;
                        }
                        else
                        {
                            // Same tip, but the strip around it moved: unread cleared, a tick
                            // advanced over the same message, a name resolved. None of those
                            // touch the tip, and before this they never reached disk at all -
                            // the badge came back on the next start.
                            conn.Execute(
                                "UPDATE history_chat_preview SET Status = ?, DeletedAtUtc = ?, " +
                                "UnreadCount = ?, Name = ?, LastMessageSendState = ?, IsGroup = ?, " +
                                "UpdatedAtUtc = ? WHERE Jid = ?",
                                (int)status,
                                carried,
                                Math.Max(0, model.UnreadCount),
                                model.Name,
                                (int)model.LastMessageSendState,
                                model.IsGroup,
                                DateTime.UtcNow,
                                jid);
                            flagUpdates++;
                            upserted++;
                            notified.Add(CloneForNotify(model, status));
                        }

                        prior.Status = (int)status;
                        prior.DeletedAtUtc = carried;
                        prior.UnreadCount = Math.Max(0, model.UnreadCount);
                        prior.Name = model.Name;
                        prior.LastMessageSendState = (int)model.LastMessageSendState;
                        prior.IsGroup = model.IsGroup;
                    }
                }).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            if (notified.Count > 0)
            {
                Debug.WriteLine(
                    "[HistoryChatPreviewStore] Delta upserted=" + upserted +
                    " inserted=" + inserted +
                    " tip=" + tipUpdates +
                    " flags=" + flagUpdates +
                    " skipped=" + skipped +
                    " notified=" + notified.Count +
                    " syncId=" + (syncId ?? "") +
                    " type=" + (syncType ?? "") +
                    " notify=" + notifyChunk);
                if (notifyChunk)
                {
                    ChunkPersisted?.Invoke(this, new HistoryChatPreviewChunkEventArgs
                    {
                        SyncId = syncId ?? string.Empty,
                        SyncType = syncType ?? string.Empty,
                        UpsertedCount = Math.Max(upserted, notified.Count),
                        Rows = notified
                    });
                }
            }
            else if (skipped > 0)
            {
                Debug.WriteLine(
                    "[HistoryChatPreviewStore] Delta no-op skipped=" + skipped +
                    " type=" + (syncType ?? ""));
            }
        }

        /// <summary>
        /// The addresses to restrict the pre-write reads to, or null to read the whole table.
        /// </summary>
        /// <remarks>
        /// A sync chunk carries more addresses than SQLite will take as bound parameters, and at
        /// that size scanning is cheaper than a huge IN list anyway. A slice carries one or two,
        /// and that is the case worth narrowing: on eMMC two full scans to save one row is the
        /// cost the slice path was introduced to get away from.
        /// </remarks>
        private static string[] ScopeFor(IReadOnlyList<HistoryChatPreview> rows)
        {
            const int MaxScopedAddresses = 64;
            if (rows == null || rows.Count > MaxScopedAddresses)
            {
                return null;
            }

            var scope = new List<string>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                string jid = rows[i]?.Jid;
                if (!string.IsNullOrWhiteSpace(jid))
                {
                    scope.Add(jid);
                }
            }

            return scope.Count == 0 ? null : scope.ToArray();
        }

        private static string WhereJidIn(string[] scope)
        {
            if (scope == null || scope.Length == 0)
            {
                return string.Empty;
            }

            var placeholders = new string[scope.Length];
            for (int i = 0; i < scope.Length; i++)
            {
                placeholders[i] = "?";
            }

            return " WHERE Jid IN (" + string.Join(", ", placeholders) + ")";
        }

        private async Task<Dictionary<string, LifecycleSnapshot>> LoadLifecycleSnapshotAsync(string[] scope)
        {
            var map = new Dictionary<string, LifecycleSnapshot>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var rows = await _connection
                    .QueryAsync<LifecycleSnapshot>(
                        "SELECT Jid, Status, DeletedAtUtc, LastMessageTimestampUtc, LastMessageId, " +
                        "UnreadCount, Name, LastMessageSendState, IsGroup, LidJid, PnJid " +
                        "FROM history_chat_preview" + WhereJidIn(scope),
                        scope ?? new string[0])
                    .ConfigureAwait(false);
                if (rows != null)
                {
                    for (int i = 0; i < rows.Count; i++)
                    {
                        LifecycleSnapshot row = rows[i];
                        if (row != null && !string.IsNullOrWhiteSpace(row.Jid))
                        {
                            map[row.Jid] = row;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HistoryChatPreviewStore] LoadLifecycleSnapshot failed: " + ex.Message);
            }

            return map;
        }

        private static bool IsIncomingTipNewer(LifecycleSnapshot prior, HistoryChatPreview model)
        {
            if (prior == null || model == null)
            {
                return true;
            }

            DateTime? incoming = model.LastMessageTimestampUtc;
            DateTime? existing = prior.LastMessageTimestampUtc;
            if (incoming.HasValue && (!existing.HasValue || incoming.Value > existing.Value))
            {
                return true;
            }

            if (incoming.HasValue &&
                existing.HasValue &&
                incoming.Value == existing.Value &&
                !string.IsNullOrWhiteSpace(model.LastMessageId) &&
                !string.Equals(model.LastMessageId, prior.LastMessageId, StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Whether anything the list strip shows changed without the tip moving.
        /// </summary>
        /// <remarks>
        /// This gate used to ask only about the tip. Everything else the row carries - the
        /// unread badge, the delivery tick, the resolved name, the group flag - changes over
        /// a conversation whose newest message stays put, which is the ordinary case: reading
        /// a chat does not add a message to it.
        /// </remarks>
        private static bool HasStripChange(LifecycleSnapshot prior, HistoryChatPreview model)
        {
            if (prior == null || model == null)
            {
                return true;
            }

            return prior.UnreadCount != Math.Max(0, model.UnreadCount) ||
                   prior.LastMessageSendState != (int)model.LastMessageSendState ||
                   prior.IsGroup != model.IsGroup ||
                   !string.Equals(prior.Name ?? string.Empty, model.Name ?? string.Empty, StringComparison.Ordinal);
        }

        /// <summary>
        /// Keeps a value the incoming row has no way of knowing rather than blanking it.
        /// </summary>
        private static string Carry(string incoming, string stored)
        {
            return string.IsNullOrWhiteSpace(incoming) ? stored : incoming;
        }

        private static bool NullableDateEquals(DateTime? left, DateTime? right)
        {
            if (!left.HasValue && !right.HasValue)
            {
                return true;
            }

            if (!left.HasValue || !right.HasValue)
            {
                return false;
            }

            return left.Value == right.Value;
        }

        private static HistoryChatPreview CloneForNotify(HistoryChatPreview model, ChatStatus status)
        {
            return new HistoryChatPreview
            {
                Jid = model.Jid,
                LidJid = model.LidJid,
                PnJid = model.PnJid,
                Name = model.Name,
                IsGroup = model.IsGroup,
                Status = status,
                UnreadCount = model.UnreadCount,
                IsChatPinned = model.IsChatPinned,
                PinnedTimestamp = model.PinnedTimestamp,
                AppliesMute = model.AppliesMute,
                MutedUntil = model.MutedUntil,
                LastMessage = model.LastMessage,
                LastMessageAuthor = model.LastMessageAuthor,
                LastMessageIsFromMe = model.LastMessageIsFromMe,
                LastMessageSenderName = model.LastMessageSenderName,
                LastMessageParticipantJid = model.LastMessageParticipantJid,
                LastMessageKind = model.LastMessageKind,
                LastMessageSendState = model.LastMessageSendState,
                LastMessageMentionedJids = model.LastMessageMentionedJids,
                LastMessageTimestampUtc = model.LastMessageTimestampUtc,
                LastMessageId = model.LastMessageId,
                SyncId = model.SyncId,
                SyncType = model.SyncType,
                UpdatedAtUtc = model.UpdatedAtUtc
            };
        }

        /// <summary>
        /// Pin / mute travel on the in-memory preview only (no columns on this table) and still
        /// need to reach the list when the tip itself did not move.
        /// </summary>
        private static bool HasLocalFlagPayload(HistoryChatPreview model)
        {
            return model != null && (model.IsChatPinned.HasValue || model.AppliesMute);
        }

        private sealed class LifecycleSnapshot
        {
            public string Jid { get; set; }

            public int Status { get; set; }

            public DateTime? DeletedAtUtc { get; set; }

            public DateTime? LastMessageTimestampUtc { get; set; }

            public string LastMessageId { get; set; }

            public int UnreadCount { get; set; }

            public string Name { get; set; }

            public int LastMessageSendState { get; set; }

            public bool IsGroup { get; set; }

            // Carried so a live catalog write does not blank them: the live row builder has
            // no idea what the other half of a PN/LID pair is, only the history sync does.
            public string LidJid { get; set; }

            public string PnJid { get; set; }
        }

        public async Task<IReadOnlyList<HistoryChatPreview>> GetAllAsync(string syncId = null)
        {
            await EnsureInitializedAsync().ConfigureAwait(false);
            List<HistoryChatPreviewRow> rows;
            if (string.IsNullOrWhiteSpace(syncId))
            {
                rows = await _connection.Table<HistoryChatPreviewRow>()
                    .Where(r => r.Status != (int)ChatStatus.Deleted)
                    .OrderByDescending(r => r.LastMessageTimestampUtc)
                    .ToListAsync()
                    .ConfigureAwait(false);
            }
            else
            {
                rows = await _connection.Table<HistoryChatPreviewRow>()
                    .Where(r => r.SyncId == syncId && r.Status != (int)ChatStatus.Deleted)
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
                return await _connection.Table<HistoryChatPreviewRow>()
                    .Where(r => r.Status != (int)ChatStatus.Deleted)
                    .CountAsync()
                    .ConfigureAwait(false);
            }

            return await _connection.Table<HistoryChatPreviewRow>()
                .Where(r => r.SyncId == syncId && r.Status != (int)ChatStatus.Deleted)
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

        public async Task MarkDeletedAsync(IReadOnlyList<string> jids, DateTime deletedAtUtc)
        {
            if (jids == null || jids.Count == 0)
            {
                return;
            }

            await EnsureInitializedAsync().ConfigureAwait(false);
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                int marked = 0;
                foreach (var jid in jids)
                {
                    if (string.IsNullOrWhiteSpace(jid))
                    {
                        continue;
                    }

                    marked += await _connection.ExecuteAsync(
                            "UPDATE history_chat_preview SET DeletedAtUtc = ?, Status = ? WHERE Jid = ?",
                            deletedAtUtc,
                            (int)ChatStatus.Deleted,
                            jid)
                        .ConfigureAwait(false);
                }

                Debug.WriteLine("[HistoryChatPreviewStore] Tombstoned " + marked + " row(s)");
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public async Task SetStatusAsync(IReadOnlyList<string> jids, ChatStatus status)
        {
            if (jids == null || jids.Count == 0 || status == ChatStatus.Deleted)
            {
                return;
            }

            await EnsureInitializedAsync().ConfigureAwait(false);
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (string jid in jids)
                {
                    if (string.IsNullOrWhiteSpace(jid))
                    {
                        continue;
                    }

                    // A valid deletion tombstone wins over archive/unarchive app-state.
                    await _connection.ExecuteAsync(
                            "UPDATE history_chat_preview SET Status = ? WHERE Jid = ? AND DeletedAtUtc IS NULL",
                            (int)status,
                            jid)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// The tombstone per JID, so an upsert can decide whether the incoming row is a leftover
        /// from a sync chunk or a genuinely newer message that should bring the chat back.
        /// </summary>
        private async Task<Dictionary<string, DateTime>> LoadTombstonesAsync(string[] scope)
        {
            var map = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string scopeClause = scope == null || scope.Length == 0
                    ? string.Empty
                    : WhereJidIn(scope).Replace(" WHERE ", " AND ");
                var rows = await _connection
                    .QueryAsync<TombstoneRow>(
                        "SELECT Jid, DeletedAtUtc FROM history_chat_preview WHERE DeletedAtUtc IS NOT NULL" +
                        scopeClause,
                        scope ?? new string[0])
                    .ConfigureAwait(false);
                if (rows != null)
                {
                    foreach (var row in rows)
                    {
                        if (row != null && !string.IsNullOrWhiteSpace(row.Jid) && row.DeletedAtUtc.HasValue)
                        {
                            map[row.Jid] = row.DeletedAtUtc.Value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HistoryChatPreviewStore] LoadTombstones failed: " + ex.Message);
            }

            return map;
        }

        /// <summary>
        /// Keeps the chat deleted unless the incoming preview is newer than the deletion. A message
        /// that arrives after the user deleted the conversation is meant to bring it back; anything
        /// older is history the deletion already covered.
        /// </summary>
        private static DateTime? CarriedTombstone(
            Dictionary<string, DateTime> tombstones,
            HistoryChatPreview model)
        {
            if (tombstones.Count == 0 || string.IsNullOrWhiteSpace(model.Jid))
            {
                return null;
            }

            DateTime deletedAt;
            if (!tombstones.TryGetValue(model.Jid, out deletedAt))
            {
                return null;
            }

            var incoming = model.LastMessageTimestampUtc;

            return incoming.HasValue && incoming.Value > deletedAt ? (DateTime?)null : deletedAt;
        }

        private static ChatStatus ResolveStatus(
            Dictionary<string, DateTime> tombstones,
            HistoryChatPreview model,
            DateTime? carriedTombstone)
        {
            if (carriedTombstone.HasValue)
            {
                return ChatStatus.Deleted;
            }

            // A message newer than the tombstone explicitly revives the conversation.
            if (!string.IsNullOrWhiteSpace(model.Jid) && tombstones.ContainsKey(model.Jid))
            {
                return ChatStatus.Active;
            }

            return model.Status;
        }

        private sealed class TombstoneRow
        {
            public string Jid { get; set; }
            public DateTime? DeletedAtUtc { get; set; }
        }

        private async Task EnsureInitializedAsync()
        {
            if (!_initialized)
            {
                await InitializeAsync().ConfigureAwait(false);
            }
        }

        private async Task EnsureColumnAsync(string column, string sqlType)
        {
            try
            {
                List<SqliteTableInfoRow> cols = await _connection
                    .QueryAsync<SqliteTableInfoRow>("PRAGMA table_info(history_chat_preview)")
                    .ConfigureAwait(false);
                bool present = false;
                if (cols != null)
                {
                    for (int i = 0; i < cols.Count; i++)
                    {
                        if (string.Equals(cols[i]?.name, column, StringComparison.OrdinalIgnoreCase))
                        {
                            present = true;
                            break;
                        }
                    }
                }

                if (!present)
                {
                    await _connection.ExecuteAsync(
                            "ALTER TABLE history_chat_preview ADD COLUMN " + column + " " + sqlType)
                        .ConfigureAwait(false);
                    Debug.WriteLine("[HistoryChatPreviewStore] Added " + column + " column");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HistoryChatPreviewStore] EnsureColumn " + column + " failed: " + ex.Message);
            }
        }

        private sealed class SqliteTableInfoRow
        {
            public int cid { get; set; }
            public string name { get; set; }
            public string type { get; set; }
        }

        private static HistoryChatPreviewRow ToRow(
            HistoryChatPreview model,
            DateTime? deletedAtUtc,
            ChatStatus status,
            LifecycleSnapshot prior = null)
        {
            return new HistoryChatPreviewRow
            {
                DeletedAtUtc = deletedAtUtc,
                Jid = model.Jid,

                // InsertOrReplace rewrites the whole row, and the live catalog builder cannot
                // fill these two - only the history sync learns which PN goes with which LID.
                // Written straight from the model, the first live write after a sync blanked
                // them and the contact came back as two rows.
                LidJid = Carry(model.LidJid, prior?.LidJid),
                PnJid = Carry(model.PnJid, prior?.PnJid),
                Name = model.Name,
                IsGroup = model.IsGroup,
                Status = (int)status,
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

            ChatStatus status = Enum.IsDefined(typeof(ChatStatus), row.Status)
                ? (ChatStatus)row.Status
                : ChatStatus.Active;

            return new HistoryChatPreview
            {
                Jid = row.Jid,
                LidJid = row.LidJid,
                PnJid = row.PnJid,
                Name = row.Name,
                IsGroup = row.IsGroup,
                Status = status,
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
