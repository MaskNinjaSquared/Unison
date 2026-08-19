using System;
using System.Collections.Concurrent;
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

        public event EventHandler<string> PersonChanged;

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
                await _connection.CreateTableAsync<PersonGroupRow>().ConfigureAwait(false);
                await EnsurePhoneIndexAsync().ConfigureAwait(false);
                await EnsurePersonGroupIndexesAsync().ConfigureAwait(false);
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

        public async Task<IReadOnlyList<Person>> FindByPhoneAsync(string digits)
        {
            string phone = PhoneNumberHelper.NormalizePhoneDigits(digits);
            if (string.IsNullOrEmpty(phone))
            {
                return Array.Empty<Person>();
            }

            await EnsureInitializedAsync().ConfigureAwait(false);

            List<PersonRow> rows = await _connection.QueryAsync<PersonRow>(
                "SELECT * FROM Person WHERE Phone = ?",
                phone).ConfigureAwait(false);

            return ToModels(rows);
        }

        public async Task<IReadOnlyList<Person>> ListWithPhoneAsync()
        {
            await EnsureInitializedAsync().ConfigureAwait(false);

            List<PersonRow> rows = await _connection.QueryAsync<PersonRow>(
                "SELECT * FROM Person WHERE Phone IS NOT NULL AND Phone != ''").ConfigureAwait(false);

            return ToModels(rows);
        }

        public async Task<bool> UpsertIfChangedAsync(
            string jid,
            string name,
            string avatarUrl,
            string phone,
            PersonSource source)
        {
            string key = NormalizeJid(jid);
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            string normalizedPhone = PhoneNumberHelper.NormalizePhoneDigits(phone);

            await EnsureInitializedAsync().ConfigureAwait(false);

            bool changed = false;
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

                if (!Person.RequiresUpdate(existing, name, avatarUrl, normalizedPhone, source))
                {
                    return false;
                }

                Person next = existing != null ? Clone(existing) : new Person { Jid = key };
                next.Jid = key;
                next.Source = Person.Promote(existing != null ? existing.Source : PersonSource.Unknown, source);

                if (Person.CanWriteName(existing != null ? existing.Source : PersonSource.Unknown, source) &&
                    !string.IsNullOrWhiteSpace(name))
                {
                    next.Name = name.Trim();
                }

                if (!string.IsNullOrWhiteSpace(avatarUrl))
                {
                    next.AvatarUrl = avatarUrl.Trim();
                }

                if (!string.IsNullOrWhiteSpace(normalizedPhone))
                {
                    next.Phone = normalizedPhone;
                }

                next.UpdatedAtUtc = DateTime.UtcNow;

                await _connection.InsertOrReplaceAsync(ToRow(next)).ConfigureAwait(false);
                _cache[key] = Clone(next);
                changed = true;
            }
            finally
            {
                _writeLock.Release();
            }

            // Raise after the lock: subscribers may re-enter the store to read the new value.
            if (changed)
            {
                PersonChanged?.Invoke(this, key);
            }

            return changed;
        }

        public async Task ReplaceGroupMembershipsAsync(
            string groupJid,
            IReadOnlyList<PersonGroupMembership> members)
        {
            string groupKey = NormalizeJid(groupJid);
            if (string.IsNullOrEmpty(groupKey) || !JidHelper.IsGroupJid(groupKey))
            {
                return;
            }

            await EnsureInitializedAsync().ConfigureAwait(false);
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _connection.ExecuteAsync(
                    "DELETE FROM PersonGroup WHERE GroupJid = ?",
                    groupKey).ConfigureAwait(false);

                if (members == null || members.Count == 0)
                {
                    return;
                }

                DateTime now = DateTime.UtcNow;
                foreach (var member in members)
                {
                    if (member == null)
                    {
                        continue;
                    }

                    string personKey = NormalizeJid(member.PersonJid);
                    if (string.IsNullOrEmpty(personKey) || JidHelper.IsGroupJid(personKey))
                    {
                        continue;
                    }

                    var row = new PersonGroupRow
                    {
                        Id = PersonGroupRow.MakeId(personKey, groupKey),
                        PersonJid = personKey,
                        GroupJid = groupKey,
                        Role = (int)member.Role,
                        UpdatedAtUtc = now
                    };
                    await _connection.InsertOrReplaceAsync(row).ConfigureAwait(false);
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public async Task<IReadOnlyList<PersonGroupMembership>> ListGroupsForPersonAsync(string personJid)
        {
            string personKey = NormalizeJid(personJid);
            if (string.IsNullOrEmpty(personKey))
            {
                return Array.Empty<PersonGroupMembership>();
            }

            await EnsureInitializedAsync().ConfigureAwait(false);

            List<PersonGroupRow> rows = await _connection.QueryAsync<PersonGroupRow>(
                "SELECT * FROM PersonGroup WHERE PersonJid = ? ORDER BY UpdatedAtUtc DESC",
                personKey).ConfigureAwait(false);

            if (rows == null || rows.Count == 0)
            {
                return Array.Empty<PersonGroupMembership>();
            }

            var list = new List<PersonGroupMembership>(rows.Count);
            foreach (var row in rows)
            {
                if (row == null || string.IsNullOrWhiteSpace(row.GroupJid))
                {
                    continue;
                }

                GroupParticipantRole role = GroupParticipantRole.Member;
                if (row.Role >= (int)GroupParticipantRole.Member &&
                    row.Role <= (int)GroupParticipantRole.SuperAdmin)
                {
                    role = (GroupParticipantRole)row.Role;
                }

                list.Add(new PersonGroupMembership
                {
                    PersonJid = row.PersonJid,
                    GroupJid = row.GroupJid,
                    Role = role,
                    UpdatedAtUtc = row.UpdatedAtUtc
                });
            }

            return list;
        }

        private async Task EnsurePhoneIndexAsync()
        {
            try
            {
                await _connection.CreateIndexAsync("IX_Person_Phone", "Person", "Phone", unique: false)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[PersonStore] Phone index: " + ex.Message);
            }
        }

        private async Task EnsurePersonGroupIndexesAsync()
        {
            try
            {
                await _connection.CreateIndexAsync(
                    "IX_PersonGroup_Person", "PersonGroup", "PersonJid", unique: false).ConfigureAwait(false);
                await _connection.CreateIndexAsync(
                    "IX_PersonGroup_Group", "PersonGroup", "GroupJid", unique: false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[PersonStore] PersonGroup indexes: " + ex.Message);
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

        private static IReadOnlyList<Person> ToModels(List<PersonRow> rows)
        {
            if (rows == null || rows.Count == 0)
            {
                return Array.Empty<Person>();
            }

            var list = new List<Person>(rows.Count);
            foreach (var row in rows)
            {
                Person person = ToModel(row);
                if (person != null)
                {
                    list.Add(person);
                }
            }

            return list;
        }

        private static Person ToModel(PersonRow row)
        {
            if (row == null)
            {
                return null;
            }

            PersonSource source = PersonSource.Unknown;
            if (row.Source >= (int)PersonSource.Unknown && row.Source <= (int)PersonSource.AddressBook)
            {
                source = (PersonSource)row.Source;
            }

            return new Person
            {
                Jid = row.Jid,
                Name = row.Name,
                AvatarUrl = row.AvatarUrl,
                Phone = row.Phone,
                Source = source,
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
                Source = (int)person.Source,
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
                Source = source.Source,
                UpdatedAtUtc = source.UpdatedAtUtc
            };
        }
    }
}
