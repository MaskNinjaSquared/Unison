using System;
using System.Diagnostics;
using System.IO;
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
    /// SQLite <c>history_migration</c> gate (same <c>unison.db</c> as Person/Chat).
    /// </summary>
    public sealed class HistoryMigrationStore : IHistoryMigrationStore
    {
        private static readonly string DatabaseFileName = "unison.db";

        private readonly SemaphoreSlim _initLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        private SQLiteAsyncConnection _connection;
        private bool _initialized;

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
                await _connection.CreateTableAsync<HistoryMigrationRow>().ConfigureAwait(false);
                await EnsureDefaultRowAsync().ConfigureAwait(false);
                _initialized = true;
                Debug.WriteLine("[HistoryMigrationStore] Initialized at " + dbPath);
            }
            finally
            {
                _initLock.Release();
            }
        }

        public async Task<HistoryMigrationState> GetAsync()
        {
            await EnsureInitializedAsync().ConfigureAwait(false);
            var row = await _connection.FindAsync<HistoryMigrationRow>(HistoryMigrationState.DefaultId)
                .ConfigureAwait(false);
            return ToModel(row) ?? CreatePending();
        }

        public async Task MarkInProgressAsync(string syncId, string syncType, string reason = null)
        {
            await EnsureInitializedAsync().ConfigureAwait(false);
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var row = await LoadOrCreateRowAsync().ConfigureAwait(false);
                if (row.Status == (int)HistoryMigrationStatus.Succeeded &&
                    string.Equals(row.SyncId ?? string.Empty, syncId ?? string.Empty, StringComparison.Ordinal))
                {
                    return;
                }

                DateTime now = DateTime.UtcNow;
                if (row.Status != (int)HistoryMigrationStatus.InProgress ||
                    !string.Equals(row.SyncId ?? string.Empty, syncId ?? string.Empty, StringComparison.Ordinal))
                {
                    row.StartedAtUtc = now;
                }

                row.Status = (int)HistoryMigrationStatus.InProgress;
                row.SyncId = syncId ?? string.Empty;
                row.SyncType = syncType ?? string.Empty;
                row.CompletedAtUtc = null;
                row.Error = string.IsNullOrWhiteSpace(reason) ? null : reason;
                await _connection.InsertOrReplaceAsync(row).ConfigureAwait(false);
                Debug.WriteLine(
                    "[HistoryMigrationStore] InProgress syncId=" + (syncId ?? "") +
                    " type=" + (syncType ?? "") +
                    " reason=" + (reason ?? ""));
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public async Task MarkSucceededAsync(string syncId, string syncType, int conversationCount)
        {
            await EnsureInitializedAsync().ConfigureAwait(false);
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var row = await LoadOrCreateRowAsync().ConfigureAwait(false);
                DateTime now = DateTime.UtcNow;
                if (!row.StartedAtUtc.HasValue)
                {
                    row.StartedAtUtc = now;
                }

                row.Status = (int)HistoryMigrationStatus.Succeeded;
                row.SyncId = syncId ?? string.Empty;
                row.SyncType = syncType ?? string.Empty;
                row.ConversationCount = Math.Max(0, conversationCount);
                row.CompletedAtUtc = now;
                row.Error = null;
                await _connection.InsertOrReplaceAsync(row).ConfigureAwait(false);
                Debug.WriteLine(
                    "[HistoryMigrationStore] Succeeded syncId=" + (syncId ?? "") +
                    " type=" + (syncType ?? "") +
                    " conversations=" + conversationCount);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public async Task MarkFailedAsync(string syncId, string error, string syncType = null)
        {
            await EnsureInitializedAsync().ConfigureAwait(false);
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var row = await LoadOrCreateRowAsync().ConfigureAwait(false);
                row.Status = (int)HistoryMigrationStatus.Failed;
                row.SyncId = syncId ?? row.SyncId ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(syncType))
                {
                    row.SyncType = syncType;
                }

                row.Error = error ?? string.Empty;
                row.CompletedAtUtc = DateTime.UtcNow;
                await _connection.InsertOrReplaceAsync(row).ConfigureAwait(false);
                Debug.WriteLine("[HistoryMigrationStore] Failed: " + (error ?? ""));
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public async Task ResetAsync(string reason = null)
        {
            await EnsureInitializedAsync().ConfigureAwait(false);
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var row = new HistoryMigrationRow
                {
                    Id = HistoryMigrationState.DefaultId,
                    Status = (int)HistoryMigrationStatus.Pending,
                    SyncId = string.Empty,
                    SyncType = string.Empty,
                    SchemaVersion = 0,
                    ConversationCount = 0,
                    StartedAtUtc = null,
                    CompletedAtUtc = null,
                    Error = string.IsNullOrWhiteSpace(reason) ? null : reason
                };
                await _connection.InsertOrReplaceAsync(row).ConfigureAwait(false);
                Debug.WriteLine("[HistoryMigrationStore] Reset reason=" + (reason ?? ""));
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

        private async Task EnsureDefaultRowAsync()
        {
            var existing = await _connection.FindAsync<HistoryMigrationRow>(HistoryMigrationState.DefaultId)
                .ConfigureAwait(false);
            if (existing != null)
            {
                return;
            }

            await _connection.InsertAsync(new HistoryMigrationRow
            {
                Id = HistoryMigrationState.DefaultId,
                Status = (int)HistoryMigrationStatus.Pending,
                SyncId = string.Empty,
                SyncType = string.Empty,
                SchemaVersion = 0,
                ConversationCount = 0
            }).ConfigureAwait(false);
        }

        private async Task<HistoryMigrationRow> LoadOrCreateRowAsync()
        {
            var row = await _connection.FindAsync<HistoryMigrationRow>(HistoryMigrationState.DefaultId)
                .ConfigureAwait(false);
            if (row != null)
            {
                return row;
            }

            row = new HistoryMigrationRow
            {
                Id = HistoryMigrationState.DefaultId,
                Status = (int)HistoryMigrationStatus.Pending,
                SyncId = string.Empty,
                SyncType = string.Empty
            };
            await _connection.InsertAsync(row).ConfigureAwait(false);
            return row;
        }

        private static HistoryMigrationState CreatePending()
        {
            return new HistoryMigrationState
            {
                Id = HistoryMigrationState.DefaultId,
                Status = HistoryMigrationStatus.Pending
            };
        }

        private static HistoryMigrationState ToModel(HistoryMigrationRow row)
        {
            if (row == null)
            {
                return null;
            }

            HistoryMigrationStatus status = HistoryMigrationStatus.Pending;
            if (row.Status >= (int)HistoryMigrationStatus.Pending &&
                row.Status <= (int)HistoryMigrationStatus.Failed)
            {
                status = (HistoryMigrationStatus)row.Status;
            }

            return new HistoryMigrationState
            {
                Id = row.Id ?? HistoryMigrationState.DefaultId,
                Status = status,
                SyncId = row.SyncId,
                SyncType = row.SyncType,
                SchemaVersion = row.SchemaVersion,
                ConversationCount = row.ConversationCount,
                StartedAtUtc = row.StartedAtUtc,
                CompletedAtUtc = row.CompletedAtUtc,
                Error = row.Error
            };
        }
    }
}
