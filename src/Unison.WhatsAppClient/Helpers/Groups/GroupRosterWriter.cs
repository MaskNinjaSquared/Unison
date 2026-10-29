// =============================================================================
// GroupRosterWriter
//
// Writes an applied group roster and the person-to-group rows it implies. Both
// writes are fire-and-forget: the roster on screen is already right, and a
// failed save only means the next cold start restores an older one.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class GroupRosterWriter
    {
        private readonly IGroupRosterStore _rosters;
        private readonly IPersonStore _people;

        public GroupRosterWriter(IGroupRosterStore rosters, IPersonStore people)
        {
            _rosters = rosters ?? throw new ArgumentNullException(nameof(rosters));
            _people = people ?? throw new ArgumentNullException(nameof(people));
        }

        public void SaveRoster(string groupJid, List<GroupMember> snapshot)
        {
            if (snapshot == null || snapshot.Count == 0)
            {
                return;
            }

            _ = SaveRosterAsync(groupJid, snapshot);
        }

        public void SaveMemberships(string groupJid, List<PersonGroupMembership> rows)
        {
            if (rows == null || rows.Count == 0)
            {
                return;
            }

            _ = SaveMembershipsAsync(groupJid, rows);
        }

        /// <summary>
        /// Writes only what a flush changed: the members that came or changed, the ones that left,
        /// and their person-to-group rows. Detached copies only.
        /// </summary>
        public Task SaveChangesAsync(
            string groupJid,
            IReadOnlyList<GroupMember> upserted,
            IReadOnlyList<string> removedJids,
            IReadOnlyList<PersonGroupMembership> memberships,
            IReadOnlyList<string> removedPersonKeys)
        {
            bool nothing =
                (upserted == null || upserted.Count == 0) &&
                (removedJids == null || removedJids.Count == 0);
            return nothing
                ? Task.CompletedTask
                : SaveChangesCoreAsync(groupJid, upserted, removedJids, memberships, removedPersonKeys);
        }

        private async Task SaveChangesCoreAsync(
            string groupJid,
            IReadOnlyList<GroupMember> upserted,
            IReadOnlyList<string> removedJids,
            IReadOnlyList<PersonGroupMembership> memberships,
            IReadOnlyList<string> removedPersonKeys)
        {
            try
            {
                await _rosters.InitializeAsync().ConfigureAwait(false);
                if (removedJids != null && removedJids.Count > 0)
                {
                    await _rosters.RemoveMembersAsync(groupJid, removedJids).ConfigureAwait(false);
                }

                if (upserted != null && upserted.Count > 0)
                {
                    await _rosters.UpsertMembersAsync(groupJid, upserted).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[GroupRosterWriter] Incremental roster persist failed: " + ex.Message);
            }

            try
            {
                await _people.InitializeAsync().ConfigureAwait(false);
                if (removedPersonKeys != null && removedPersonKeys.Count > 0)
                {
                    await _people.RemoveGroupMembershipsAsync(groupJid, removedPersonKeys).ConfigureAwait(false);
                }

                if (memberships != null && memberships.Count > 0)
                {
                    await _people.UpsertGroupMembershipsAsync(groupJid, memberships).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[GroupRosterWriter] Incremental PersonGroup persist failed: " + ex.Message);
            }
        }

        private async Task SaveRosterAsync(string groupJid, List<GroupMember> members)
        {
            try
            {
                await _rosters.InitializeAsync().ConfigureAwait(false);
                await _rosters.ReplaceForGroupAsync(groupJid, members).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[GroupRosterWriter] Group roster persist failed: " + ex.Message);
            }
        }

        private async Task SaveMembershipsAsync(string groupJid, List<PersonGroupMembership> members)
        {
            try
            {
                await _people.InitializeAsync().ConfigureAwait(false);
                await _people.ReplaceGroupMembershipsAsync(groupJid, members).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[GroupRosterWriter] PersonGroup persist failed: " + ex.Message);
            }
        }
    }
}
