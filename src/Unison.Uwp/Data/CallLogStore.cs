using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SQLite;
using Unison.Core.Contracts;
using Unison.Core.Helpers.Calls;
using Unison.Core.Models;
using Unison.Uwp.Data.Entities;
using Windows.Storage;

namespace Unison.Uwp.Data
{
    /// <summary>SQLite <c>call_log</c> in <c>unison.db</c> — Recents history across restarts.</summary>
    public sealed class CallLogStore : ICallLogStore
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
                await _connection.CreateTableAsync<CallLogRow>().ConfigureAwait(false);
                await EnsureColumnAsync("DurationSeconds", "INTEGER NOT NULL DEFAULT 0")
                    .ConfigureAwait(false);
                _initialized = true;
                Debug.WriteLine("[CallLogStore] Initialized at " + dbPath);
            }
            finally
            {
                _initLock.Release();
            }
        }

        public async Task<IReadOnlyList<CallHistoryEntry>> LoadAllAsync()
        {
            await EnsureInitializedAsync().ConfigureAwait(false);
            List<CallLogRow> rows = await _connection.Table<CallLogRow>()
                .OrderByDescending(r => r.TimestampUtc)
                .ToListAsync()
                .ConfigureAwait(false);

            var list = new List<CallHistoryEntry>(rows.Count);
            foreach (CallLogRow row in rows)
            {
                CallHistoryEntry entry = ToModel(row);
                if (entry != null)
                {
                    list.Add(entry);
                }
            }

            return list;
        }

        public async Task UpsertAsync(CallHistoryEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.CanonicalJid))
            {
                return;
            }

            await EnsureInitializedAsync().ConfigureAwait(false);
            CallLogRow row = ToRow(entry);
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _connection.InsertOrReplaceAsync(row).ConfigureAwait(false);
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

        private static CallLogRow ToRow(CallHistoryEntry entry)
        {
            int kind = (int)entry.ChromeKind;
            return new CallLogRow
            {
                Id = CallLogRow.MakeId(entry.CallId, kind, entry.CanonicalJid),
                CallId = entry.CallId ?? string.Empty,
                CanonicalJid = entry.CanonicalJid,
                Direction = (int)entry.Direction,
                ChromeKind = kind,
                IsVideo = entry.IsVideo,
                DurationSeconds = entry.DurationSeconds,
                TimestampUtc = entry.TimestampUtc.Kind == DateTimeKind.Utc
                    ? entry.TimestampUtc
                    : DateTime.SpecifyKind(entry.TimestampUtc, DateTimeKind.Utc),
                CategoryLabel = entry.CategoryLabel,
                TimestampLabel = entry.TimestampLabel,
                DirectionGlyph = string.IsNullOrEmpty(entry.DirectionGlyph)
                    ? CallDirectionGlyphs.For(entry.Direction)
                    : entry.DirectionGlyph
            };
        }

        private static CallHistoryEntry ToModel(CallLogRow row)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.CanonicalJid))
            {
                return null;
            }

            var direction = (CallDirection)row.Direction;
            return new CallHistoryEntry
            {
                CallId = row.CallId,
                CanonicalJid = row.CanonicalJid,
                Direction = direction,
                DirectionGlyph = string.IsNullOrEmpty(row.DirectionGlyph)
                    ? CallDirectionGlyphs.For(direction)
                    : row.DirectionGlyph,
                ChromeKind = (CallEventChromeKind)row.ChromeKind,
                IsVideo = row.IsVideo,
                DurationSeconds = row.DurationSeconds,
                TimestampUtc = DateTime.SpecifyKind(row.TimestampUtc, DateTimeKind.Utc),
                CategoryLabel = row.CategoryLabel,
                TimestampLabel = row.TimestampLabel
            };
        }

        private async Task EnsureColumnAsync(string column, string sqlType)
        {
            try
            {
                await _connection.ExecuteAsync(
                        "ALTER TABLE call_log ADD COLUMN " + column + " " + sqlType)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Column already exists on upgraded installs.
                Debug.WriteLine("[CallLogStore] EnsureColumn " + column + ": " + ex.Message);
            }
        }
    }
}
