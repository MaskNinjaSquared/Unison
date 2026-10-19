using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    /// SQLite <c>history_status</c> — Status items keyed by author JID (same unison.db).
    /// </summary>
    public sealed class HistoryStatusStore : IHistoryStatusStore
    {
        private static readonly string DatabaseFileName = "unison.db";

        public const int CurrentSchemaVersion = 3;

        private readonly SemaphoreSlim _initLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        private SQLiteAsyncConnection _connection;
        private bool _initialized;

        public int SchemaVersion => CurrentSchemaVersion;

        public event EventHandler Changed;

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
                await _connection.CreateTableAsync<HistoryStatusRow>().ConfigureAwait(false);
                await DropLegacyThumbnailColumnAsync().ConfigureAwait(false);
                await EnsureIsViewedColumnAsync().ConfigureAwait(false);
                _initialized = true;
                Debug.WriteLine("[HistoryStatusStore] Initialized schema=" + CurrentSchemaVersion + " at " + dbPath);
                await DeleteExpiredAsync().ConfigureAwait(false);
            }
            finally
            {
                _initLock.Release();
            }
        }

        public async Task UpsertManyAsync(IReadOnlyList<HistoryStatus> rows)
        {
            if (rows == null || rows.Count == 0)
            {
                return;
            }

            await EnsureInitializedAsync().ConfigureAwait(false);
            await DeleteExpiredAsync().ConfigureAwait(false);
            await _writeLock.WaitAsync().ConfigureAwait(false);
            int upserted = 0;
            try
            {
                await _connection.RunInTransactionAsync(conn =>
                {
                    foreach (var model in rows)
                    {
                        if (model == null ||
                            string.IsNullOrWhiteSpace(model.AuthorJid) ||
                            string.IsNullOrWhiteSpace(model.MessageId))
                        {
                            continue;
                        }

                        string id = HistoryStatusRow.MakeId(model.AuthorJid, model.MessageId);
                        HistoryStatusRow existingRow = conn.Find<HistoryStatusRow>(id);
                        HistoryStatus toWrite = model;
                        if (existingRow != null)
                        {
                            toWrite = HistoryStatusMediaMerge.Merge(ToModel(existingRow), model);
                        }

                        conn.InsertOrReplace(ToRow(toWrite));
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
                Debug.WriteLine("[HistoryStatusStore] Upserted " + upserted + " status item(s)");
                RaiseChanged();
            }
        }

        public async Task<IReadOnlyList<HistoryStatus>> GetActiveAsync(int limit = 200)
        {
            await EnsureInitializedAsync().ConfigureAwait(false);
            DateTime now = DateTime.UtcNow;
            int take = Math.Max(1, limit);
            List<HistoryStatusRow> rows = await _connection.Table<HistoryStatusRow>()
                .Where(r => r.ExpiresAtUtc == null || r.ExpiresAtUtc > now)
                .OrderByDescending(r => r.TimestampUtc)
                .Take(take)
                .ToListAsync()
                .ConfigureAwait(false);

            return ToModels(rows);
        }

        public async Task<IReadOnlyList<HistoryStatus>> GetActiveForAuthorAsync(string authorJid, int limit = 50)
        {
            string key = JidHelper.Normalize(authorJid);
            if (string.IsNullOrWhiteSpace(key))
            {
                return Array.Empty<HistoryStatus>();
            }

            await EnsureInitializedAsync().ConfigureAwait(false);
            DateTime now = DateTime.UtcNow;
            int take = Math.Max(1, limit);
            List<HistoryStatusRow> rows = await _connection.Table<HistoryStatusRow>()
                .Where(r =>
                    (r.AuthorJid == key || r.AuthorLid == key || r.AuthorPn == key) &&
                    (r.ExpiresAtUtc == null || r.ExpiresAtUtc > now))
                .OrderByDescending(r => r.TimestampUtc)
                .Take(take)
                .ToListAsync()
                .ConfigureAwait(false);

            return ToModels(rows);
        }

        public async Task<bool> MarkViewedAsync(string authorJid, string messageId)
        {
            string key = JidHelper.Normalize(authorJid);
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(messageId))
            {
                return false;
            }

            await EnsureInitializedAsync().ConfigureAwait(false);
            string id = HistoryStatusRow.MakeId(key, messageId.Trim());
            await _writeLock.WaitAsync().ConfigureAwait(false);
            bool changed = false;
            try
            {
                HistoryStatusRow row = await _connection.FindAsync<HistoryStatusRow>(id)
                    .ConfigureAwait(false);
                if (row == null)
                {
                    // Author may be stored under LID/PN aliases — match message id.
                    List<HistoryStatusRow> matches = await _connection.Table<HistoryStatusRow>()
                        .Where(r =>
                            r.MessageId == messageId.Trim() &&
                            (r.AuthorJid == key || r.AuthorLid == key || r.AuthorPn == key))
                        .ToListAsync()
                        .ConfigureAwait(false);
                    if (matches == null || matches.Count == 0)
                    {
                        return false;
                    }

                    row = matches[0];
                }

                if (row.IsViewed)
                {
                    return false;
                }

                row.IsViewed = true;
                row.UpdatedAtUtc = DateTime.UtcNow;
                await _connection.UpdateAsync(row).ConfigureAwait(false);
                changed = true;
            }
            finally
            {
                _writeLock.Release();
            }

            if (changed)
            {
                RaiseChanged();
            }

            return changed;
        }

        public async Task<int> DeleteExpiredAsync()
        {
            await EnsureInitializedAsync().ConfigureAwait(false);
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                DateTime now = DateTime.UtcNow;
                int removed = await _connection.ExecuteAsync(
                    "DELETE FROM history_status WHERE ExpiresAtUtc IS NOT NULL AND ExpiresAtUtc <= ?",
                    now).ConfigureAwait(false);
                if (removed > 0)
                {
                    Debug.WriteLine("[HistoryStatusStore] Deleted " + removed + " expired status item(s)");
                    RaiseChanged();
                }

                return removed;
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public async Task ClearAsync(string reason = null)
        {
            await EnsureInitializedAsync().ConfigureAwait(false);
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _connection.DeleteAllAsync<HistoryStatusRow>().ConfigureAwait(false);
                Debug.WriteLine("[HistoryStatusStore] Cleared reason=" + (reason ?? ""));
                RaiseChanged();
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private void RaiseChanged()
        {
            Changed?.Invoke(this, EventArgs.Empty);
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
                if (!await HasColumnAsync("MediaThumbnailBase64").ConfigureAwait(false))
                {
                    return;
                }

                await _connection.ExecuteAsync(
                        "ALTER TABLE history_status DROP COLUMN MediaThumbnailBase64")
                    .ConfigureAwait(false);
                Debug.WriteLine("[HistoryStatusStore] Dropped MediaThumbnailBase64 column");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HistoryStatusStore] Drop MediaThumbnailBase64 skipped: " + ex.Message);
            }
        }

        private async Task EnsureIsViewedColumnAsync()
        {
            try
            {
                if (await HasColumnAsync("IsViewed").ConfigureAwait(false))
                {
                    return;
                }

                await _connection.ExecuteAsync(
                        "ALTER TABLE history_status ADD COLUMN IsViewed INTEGER NOT NULL DEFAULT 0")
                    .ConfigureAwait(false);
                Debug.WriteLine("[HistoryStatusStore] Added IsViewed column");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HistoryStatusStore] Add IsViewed skipped: " + ex.Message);
            }
        }

        private async Task<bool> HasColumnAsync(string columnName)
        {
            List<SqliteTableInfoRow> cols = await _connection
                .QueryAsync<SqliteTableInfoRow>("PRAGMA table_info(history_status)")
                .ConfigureAwait(false);
            if (cols == null)
            {
                return false;
            }

            for (int i = 0; i < cols.Count; i++)
            {
                if (cols[i] != null &&
                    string.Equals(cols[i].name, columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private sealed class SqliteTableInfoRow
        {
            public int cid { get; set; }

            public string name { get; set; }

            public string type { get; set; }
        }

        private static IReadOnlyList<HistoryStatus> ToModels(List<HistoryStatusRow> rows)
        {
            if (rows == null || rows.Count == 0)
            {
                return Array.Empty<HistoryStatus>();
            }

            var list = new List<HistoryStatus>(rows.Count);
            foreach (var row in rows)
            {
                list.Add(ToModel(row));
            }

            return list;
        }

        private static HistoryStatusRow ToRow(HistoryStatus model)
        {
            return new HistoryStatusRow
            {
                Id = HistoryStatusRow.MakeId(model.AuthorJid, model.MessageId),
                AuthorJid = model.AuthorJid,
                AuthorLid = model.AuthorLid,
                AuthorPn = model.AuthorPn,
                MessageId = model.MessageId,
                IsFromMe = model.IsFromMe,
                PushName = model.PushName,
                Body = model.Body,
                Kind = (int)model.Kind,
                MediaUrl = model.MediaUrl,
                MediaDirectPath = model.MediaDirectPath,
                MediaKeyBase64 = model.MediaKeyBase64,
                MediaFileEncSha256Base64 = model.MediaFileEncSha256Base64,
                MediaMimeType = model.MediaMimeType,
                MediaDurationSeconds = model.MediaDurationSeconds,
                MediaFileName = model.MediaFileName,
                MediaFileLengthBytes = model.MediaFileLengthBytes,
                MediaLocalUri = model.MediaLocalUri,
                MediaPosterUri = model.MediaPosterUri,
                IsVoiceNote = model.IsVoiceNote,
                IsViewed = model.IsViewed,
                TimestampUtc = model.TimestampUtc,
                ExpiresAtUtc = model.ExpiresAtUtc,
                SyncId = model.SyncId ?? string.Empty,
                SyncType = model.SyncType ?? string.Empty,
                UpdatedAtUtc = model.UpdatedAtUtc == default ? DateTime.UtcNow : model.UpdatedAtUtc
            };
        }

        private static HistoryStatus ToModel(HistoryStatusRow row)
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

            return new HistoryStatus
            {
                AuthorJid = row.AuthorJid,
                AuthorLid = row.AuthorLid,
                AuthorPn = row.AuthorPn,
                MessageId = row.MessageId,
                IsFromMe = row.IsFromMe,
                PushName = row.PushName,
                Body = row.Body,
                Kind = kind,
                MediaUrl = row.MediaUrl,
                MediaDirectPath = row.MediaDirectPath,
                MediaKeyBase64 = row.MediaKeyBase64,
                MediaFileEncSha256Base64 = row.MediaFileEncSha256Base64,
                MediaMimeType = row.MediaMimeType,
                MediaDurationSeconds = row.MediaDurationSeconds,
                MediaFileName = row.MediaFileName,
                MediaFileLengthBytes = row.MediaFileLengthBytes,
                MediaLocalUri = row.MediaLocalUri,
                MediaPosterUri = row.MediaPosterUri,
                IsVoiceNote = row.IsVoiceNote,
                IsViewed = row.IsViewed,
                TimestampUtc = row.TimestampUtc,
                ExpiresAtUtc = row.ExpiresAtUtc,
                SyncId = row.SyncId,
                SyncType = row.SyncType,
                UpdatedAtUtc = row.UpdatedAtUtc
            };
        }
    }
}
