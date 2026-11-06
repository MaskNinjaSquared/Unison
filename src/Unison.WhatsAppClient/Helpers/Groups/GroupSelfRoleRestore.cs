// =============================================================================
// GroupSelfRoleRestore
//
// MyGroupRole is only ever set by a w:g2 reply, which is not back yet when a
// group opens (and never comes offline). Every metadata reply already persists
// the roster with roles, so this reads this account's own row — one primary-key
// lookup, PN first then LID — and puts that role on the row until the server
// answers. Once a reply has applied a role this session, the stored row loses.
// =============================================================================
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class GroupSelfRoleRestore
    {
        private readonly IGroupRosterStore _store;
        private readonly IJidResolver _jids;
        private readonly IDispatcher _ui;

        public GroupSelfRoleRestore(IGroupRosterStore store, IJidResolver jids, IDispatcher ui)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        }

        public async Task RestoreAsync(ChatItem group)
        {
            if (group == null || !group.IsGroup || group.IsMyGroupRoleFromServer ||
                string.IsNullOrWhiteSpace(group.JID))
            {
                return;
            }

            GroupMember self;
            try
            {
                self = await FindSelfRowAsync(JidHelper.Normalize(group.JID)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[GroupSelfRoleRestore] " + group.JID + ": " + ex.Message);
                return;
            }

            if (self == null)
            {
                return;
            }

            GroupParticipantRole stored = self.Role;
            await _ui.RunAsync(() =>
            {
                if (!group.IsMyGroupRoleFromServer)
                {
                    group.MyGroupRole = stored;
                }
            }).ConfigureAwait(false);
        }

        private async Task<GroupMember> FindSelfRowAsync(string groupJid)
        {
            Profile me = _jids.Self;
            if (me == null)
            {
                return null;
            }

            await _store.InitializeAsync().ConfigureAwait(false);
            string[] candidates = { me.Id, me.Lid };
            for (int i = 0; i < candidates.Length; i++)
            {
                string memberJid = JidHelper.Normalize(candidates[i]);
                if (string.IsNullOrWhiteSpace(memberJid))
                {
                    continue;
                }

                GroupMember row = await _store.GetMemberAsync(groupJid, memberJid).ConfigureAwait(false);
                if (row != null)
                {
                    return row;
                }
            }

            return null;
        }
    }
}
