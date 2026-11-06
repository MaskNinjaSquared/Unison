// =============================================================================
// GroupSelfRoleRestore — this account's role read back from its own stored
// roster row when a group opens, so an admin of an announce-only group gets the
// composer before the w:g2 reply (and offline / after a restart).
// =============================================================================
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class GroupSelfRoleRestoreTests
    {
        private const string Group = "120363000000000001@g.us";
        private const string SelfPn = "5511999999999@s.whatsapp.net";
        private const string SelfDevicePn = "5511999999999:12@s.whatsapp.net";
        private const string SelfLid = "123456789012345@lid";

        [Fact]
        public async Task Stored_admin_row_unlocks_an_announce_only_group()
        {
            var roster = new RecordingRosterStore();
            roster.Stored[SelfPn] = new GroupMember { Jid = SelfPn, Role = GroupParticipantRole.Admin };
            var chat = AnnounceGroup();

            await NewRestore(roster).RestoreAsync(chat);

            Assert.Equal(GroupParticipantRole.Admin, chat.MyGroupRole);
            Assert.False(chat.IsComposerLocked);
        }

        [Fact]
        public async Task Row_stored_under_the_lid_is_found_when_the_phone_has_none()
        {
            var roster = new RecordingRosterStore();
            roster.Stored[SelfLid] = new GroupMember { Jid = SelfLid, Role = GroupParticipantRole.SuperAdmin };
            var chat = AnnounceGroup();

            await NewRestore(roster).RestoreAsync(chat);

            Assert.Equal(GroupParticipantRole.SuperAdmin, chat.MyGroupRole);
        }

        [Fact]
        public async Task A_role_the_server_already_answered_wins_over_the_stored_row()
        {
            // The roster row can predate a demotion the w:g2 reply has just applied.
            var roster = new RecordingRosterStore();
            roster.Stored[SelfPn] = new GroupMember { Jid = SelfPn, Role = GroupParticipantRole.Admin };
            var chat = AnnounceGroup();
            chat.MyGroupRole = GroupParticipantRole.Member;
            chat.IsMyGroupRoleFromServer = true;

            await NewRestore(roster).RestoreAsync(chat);

            Assert.Equal(GroupParticipantRole.Member, chat.MyGroupRole);
        }

        [Fact]
        public async Task No_stored_self_row_leaves_the_role_alone()
        {
            var chat = AnnounceGroup();

            await NewRestore(new RecordingRosterStore()).RestoreAsync(chat);

            Assert.Equal(GroupParticipantRole.Member, chat.MyGroupRole);
            Assert.True(chat.IsComposerLocked);
        }

        [Fact]
        public async Task Rows_that_are_not_groups_are_ignored()
        {
            var roster = new RecordingRosterStore();
            roster.Stored[SelfPn] = new GroupMember { Jid = SelfPn, Role = GroupParticipantRole.Admin };
            var chat = new ChatItem { JID = "5511888888888@s.whatsapp.net" };

            await NewRestore(roster).RestoreAsync(chat);

            Assert.Equal(GroupParticipantRole.Member, chat.MyGroupRole);
        }

        private static ChatItem AnnounceGroup()
        {
            return new ChatItem { JID = Group, Kind = ChatKind.Group, IsAnnounceOnly = true };
        }

        private static GroupSelfRoleRestore NewRestore(IGroupRosterStore roster)
        {
            return new GroupSelfRoleRestore(roster, new SelfJids(), new CountingDispatcher());
        }

        private sealed class SelfJids : IJidResolver
        {
            public Profile Self => new Profile { Id = SelfDevicePn, Lid = SelfLid };

            public string GetCanonicalJid(string jid) => JidHelper.Normalize(jid) ?? jid;

            public bool TryGetAlias(string jid, out string alias)
            {
                alias = null;
                return false;
            }

            public bool IsSelfLinked(string jid) => false;
        }
    }
}
