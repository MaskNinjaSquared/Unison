using System;
using System.Collections.Concurrent;
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
    /// SQLite person store with a small in-memory cache.
    /// </summary>
    public sealed class PersonStore : IPersonStore
    {
        private static readonly string DatabaseFileName = "unison.db";

        private readonly ConcurrentDictionary<string, Person> _cache =
            new ConcurrentDictionary<string, Person>(StringComparer.OrdinalIgnoreCase);

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
                await _connection.CreateTableAsync<PersonRow>().ConfigureAwait(false);
                _initialized = true;
                Debug.WriteLine("[PersonStore] Initialized at " + dbPath);
            }
            finally
            {
                _initLock.Release();
            }
        }

        public Person TryGetCached(string jid)
        {
            string key = NormalizeJid(jid);
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }

            Person cached;
            if (_cache.TryGetValue(key, out cached))
            {
                return Clone(cached);
            }

            return null;
        }

        public async Task<Person> GetAsync(string jid)
        {
            string key = NormalizeJid(jid);
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }

            Person cached;
            if (_cache.TryGetValue(key, out cached))
            {
                return Clone(cached);
            }

            await EnsureInitializedAsync().ConfigureAwait(false);

            PersonRow row = await _connection.FindAsync<PersonRow>(key).ConfigureAwait(false);
            if (row == null)
            {
                return null;
            }

            Person person = ToModel(row);
            _cache[key] = Clone(person);
            return person;
        }

        public async Task<bool> UpsertIfChangedAsync(string jid, string name, string avatarUrl, string phone)
        {
            string key = NormalizeJid(jid);
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            await EnsureInitializedAsync().ConfigureAwait(false);

            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                Person existing = null;
                Person cached;
                if (_cache.TryGetValue(key, out cached))
                {
                    existing = cached;
                }
                else
                {
                    PersonRow row = await _connection.FindAsync<PersonRow>(key).ConfigureAwait(false);
                    if (row != null)
                    {
                        existing = ToModel(row);
                    }
                }

                if (!Person.RequiresUpdate(existing, name, avatarUrl, phone))
                {
                    return false;
                }

                Person next = existing != null ? Clone(existing) : new Person { Jid = key };
                next.Jid = key;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    next.Name = name.Trim();
                }

                if (!string.IsNullOrWhiteSpace(avatarUrl))
                {
                    next.AvatarUrl = avatarUrl.Trim();
                }

                if (!string.IsNullOrWhiteSpace(phone))
                {
                    next.Phone = phone.Trim();
                }

                next.UpdatedAtUtc = DateTime.UtcNow;

                await _connection.InsertOrReplaceAsync(ToRow(next)).ConfigureAwait(false);
                _cache[key] = Clone(next);
                return true;
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

        private static string NormalizeJid(string jid)
        {
            return JidHelper.Normalize(jid);
        }

        private static Person ToModel(PersonRow row)
        {
            if (row == null)
            {
                return null;
            }

            return new Person
            {
                Jid = row.Jid,
                Name = row.Name,
                AvatarUrl = row.AvatarUrl,
                Phone = row.Phone,
                UpdatedAtUtc = row.UpdatedAtUtc
            };
        }

        private static PersonRow ToRow(Person person)
        {
            return new PersonRow
            {
                Jid = person.Jid,
                Name = person.Name,
                AvatarUrl = person.AvatarUrl,
                Phone = person.Phone,
                UpdatedAtUtc = person.UpdatedAtUtc
            };
        }

        private static Person Clone(Person source)
        {
            if (source == null)
            {
                return null;
            }

            return new Person
            {
                Jid = source.Jid,
                Name = source.Name,
                AvatarUrl = source.AvatarUrl,
                Phone = source.Phone,
                UpdatedAtUtc = source.UpdatedAtUtc
            };
        }
    }
}
