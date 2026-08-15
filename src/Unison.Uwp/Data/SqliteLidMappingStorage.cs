// =============================================================================
// SqliteLidMappingStorage
//
// Gives LidMappingStore somewhere durable to keep its pairs.
//
// The mapping the app has today lives in a json sidecar that is filtered down to
// aliases whose chat is already loaded, so anything learned before the sidebar
// exists is thrown away and has to be asked for again next launch. A table has
// no such condition: a pair learned once is a pair kept.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SQLite;
using Unison.Socket.Abstractions;
using Unison.Uwp.Data.Entities;
using Windows.Storage;

namespace Unison.Uwp.Data
{
    public sealed class SqliteLidMappingStorage : ILidMappingStorage
    {
        private const string DatabaseFileName = "unison.db";

        private readonly SemaphoreSlim _initLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        private SQLiteAsyncConnection _connection;
        private bool _initialized;

        public async Task<IDictionary<string, string>> GetAsync(IEnumerable<string> keys)
        {
            IDictionary<string, string> found = new Dictionary<string, string>(StringComparer.Ordinal);
            if (keys == null)
            {
                return found;
            }

            var wanted = new List<string>();
            foreach (var key in keys)
            {
                if (!string.IsNullOrEmpty(key))
                {
                    wanted.Add(key);
                }
            }

            if (wanted.Count == 0)
            {
                return found;
            }

            await EnsureInitializedAsync().ConfigureAwait(false);

            try
            {
                foreach (var key in wanted)
                {
                    var row = await _connection.FindAsync<LidMappingRow>(key).ConfigureAwait(false);
                    if (row != null && !string.IsNullOrEmpty(row.Value))
                    {
                        found[key] = row.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                // A read failure means "unknown mapping", which every caller already handles.
                Debug.WriteLine("[SqliteLidMappingStorage] Read failed: " + ex.Message);
            }

            return found;
        }

        public async Task SetAsync(IDictionary<string, string> values)
        {
            if (values == null || values.Count == 0)
            {
                return;
            }

            await EnsureInitializedAsync().ConfigureAwait(false);

            var rows = new List<LidMappingRow>();
            var now = DateTime.UtcNow;
            foreach (var pair in values)
            {
                if (string.IsNullOrEmpty(pair.Key) || string.IsNullOrEmpty(pair.Value))
                {
                    continue;
                }

                rows.Add(new LidMappingRow { Key = pair.Key, Value = pair.Value, UpdatedAtUtc = now });
            }

            if (rows.Count == 0)
            {
                return;
            }

            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _connection.RunInTransactionAsync(connection =>
                {
                    foreach (var row in rows)
                    {
                        connection.InsertOrReplace(row);
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SqliteLidMappingStorage] Write failed: " + ex.Message);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task EnsureInitializedAsync()
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

                var path = Path.Combine(ApplicationData.Current.LocalFolder.Path, DatabaseFileName);
                _connection = new SQLiteAsyncConnection(path);
                await _connection.CreateTableAsync<LidMappingRow>().ConfigureAwait(false);
                _initialized = true;
                Debug.WriteLine("[SqliteLidMappingStorage] Initialized at " + path);
            }
            finally
            {
                _initLock.Release();
            }
        }
    }
}
