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
    /// SQLite group roster cache (same <c>unison.db</c> as Person / previews).
    /// </summary>
    public sealed class GroupRosterStore : IGroupRosterStore
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
                await _connection.CreateTableAsync<GroupRosterMemberRow>().ConfigureAwait(false);
                _initialized = true;
                Debug.WriteLine("[GroupRosterStore] Initialized at " + dbPath);
            }
            finally
            {
                _initLock.Release();
            }
        }

        public async Task ReplaceForGroupAsync(string groupJid, IReadOnlyList<GroupMember> members)
        {
            string groupKey = NormalizeJid(groupJid);
            if (string.IsNullOrEmpty(groupKey))
            {
                return;
            }

            await EnsureInitializedAsync().ConfigureAwait(false);
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _connection.ExecuteAsync(
                    "DELETE FROM GroupRosterMember WHERE GroupJid = ?",
                    groupKey).ConfigureAwait(false);

                if (members == null || members.Count == 0)
                {
                    return;
                }

                DateTime now = DateTime.UtcNow;
                var rows = new List<GroupRosterMemberRow>(members.Count);
                for (int i = 0; i < members.Count; i++)
                {
                    GroupMember member = members[i];
                    if (member == null || string.IsNullOrWhiteSpace(member.Jid))
                    {
                        continue;
                    }

                    string memberKey = NormalizeJid(member.Jid);
                    if (string.IsNullOrEmpty(memberKey))
                    {
                        continue;
                    }

                    rows.Add(new GroupRosterMemberRow
                    {
                        Id = GroupRosterMemberRow.MakeId(groupKey, memberKey),
                        GroupJid = groupKey,
                        MemberJid = memberKey,
                        PhoneNumber = member.PhoneNumber,
                        Lid = member.Lid,
                        DisplayName = member.DisplayName,
                        Role = (int)member.Role,
                        AvatarUrl = member.AvatarUrl,
                        AvatarFetchedAtUtc = member.AvatarFetchedAtUtc,
                        AvatarFetchFailedAtUtc = member.AvatarFetchFailedAtUtc,
                        AvatarFetchFailureReason = member.AvatarFetchFailureReason,
                        UpdatedAtUtc = now
                    });
                }

                if (rows.Count == 0)
                {
                    return;
                }

                await _connection.RunInTransactionAsync(conn =>
                {
                    for (int i = 0; i < rows.Count; i++)
                    {
                        conn.InsertOrReplace(rows[i]);
                    }
                }).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public async Task<IReadOnlyList<GroupMember>> GetForGroupAsync(string groupJid)
        {
            string groupKey = NormalizeJid(groupJid);
            if (string.IsNullOrEmpty(groupKey))
            {
                return Array.Empty<GroupMember>();
            }

            await EnsureInitializedAsync().ConfigureAwait(false);

            List<GroupRosterMemberRow> rows = await _connection.Table<GroupRosterMemberRow>()
                .Where(r => r.GroupJid == groupKey)
                .ToListAsync()
                .ConfigureAwait(false);

            if (rows == null || rows.Count == 0)
            {
                return Array.Empty<GroupMember>();
            }

            var list = new List<GroupMember>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                GroupRosterMemberRow row = rows[i];
                if (row == null || string.IsNullOrWhiteSpace(row.MemberJid))
                {
                    continue;
                }

                list.Add(new GroupMember
                {
                    Jid = row.MemberJid,
                    PhoneNumber = row.PhoneNumber,
                    Lid = row.Lid,
                    DisplayName = row.DisplayName,
                    Role = ParseRole(row.Role),
                    AvatarUrl = row.AvatarUrl,
                    AvatarFetchedAtUtc = row.AvatarFetchedAtUtc,
                    AvatarFetchFailedAtUtc = row.AvatarFetchFailedAtUtc,
                    AvatarFetchFailureReason = row.AvatarFetchFailureReason
                });
            }

            return list;
        }

        public async Task ClearAllAsync()
        {
            await EnsureInitializedAsync().ConfigureAwait(false);
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _connection.DeleteAllAsync<GroupRosterMemberRow>().ConfigureAwait(false);
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

        private static GroupParticipantRole ParseRole(int value)
        {
            if (value == (int)GroupParticipantRole.SuperAdmin)
            {
                return GroupParticipantRole.SuperAdmin;
            }

            if (value == (int)GroupParticipantRole.Admin)
            {
                return GroupParticipantRole.Admin;
            }

            return GroupParticipantRole.Member;
        }

        private static string NormalizeJid(string jid)
        {
            return JidHelper.Normalize(jid);
        }
    }
}
