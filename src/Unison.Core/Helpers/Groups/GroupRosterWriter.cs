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

namespace Unison.Core.Helpers
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
