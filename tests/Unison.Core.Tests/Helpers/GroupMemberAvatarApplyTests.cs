// =============================================================================
// Member avatar apply must not walk every roster member of every group.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class GroupMemberAvatarApplyTests
    {
        private const string TargetGroup = "120363111@g.us";
        private const string OtherGroup = "120363222@g.us";
        private const string MemberJid = "5511988887777@s.whatsapp.net";

        [Fact]
        public void Apply_with_group_limit_stamps_only_that_group()
        {
            GroupMember target = new GroupMember { Jid = MemberJid };
            GroupMember decoySamePerson = new GroupMember { Jid = MemberJid };
            var chats = new List<ChatItem>
            {
                BuildGroup(TargetGroup, target, DecoyMembers(400)),
                BuildGroup(OtherGroup, decoySamePerson, DecoyMembers(400))
            };

            int stamped = GroupMemberAvatarApply.Apply(
                chats,
                MemberJid,
                new GroupMemberAvatarFetchResult { LocalUri = "ms-appdata:///local/a.jpg" },
                jids: new PassThroughJids(),
                limitToGroupJid: TargetGroup,
                nowUtc: new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc));

            Assert.Equal(1, stamped);
            Assert.Equal("ms-appdata:///local/a.jpg", target.AvatarUrl);
            Assert.True(string.IsNullOrEmpty(decoySamePerson.AvatarUrl));
        }

        [Fact]
        public void Apply_without_limit_uses_FindGroupMember_across_groups()
        {
            GroupMember inA = new GroupMember { Jid = MemberJid };
            GroupMember inB = new GroupMember { Jid = MemberJid };
            var chats = new List<ChatItem>
            {
                BuildGroup(TargetGroup, inA, DecoyMembers(200)),
                BuildGroup(OtherGroup, inB, DecoyMembers(200)),
                new ChatItem { JID = "5511911112222@s.whatsapp.net", IsGroup = false }
            };

            int stamped = GroupMemberAvatarApply.Apply(
                chats,
                MemberJid,
                new GroupMemberAvatarFetchResult { LocalUri = "ms-appdata:///local/b.jpg" },
                jids: new PassThroughJids(),
                limitToGroupJid: null,
                nowUtc: new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc));

            Assert.Equal(2, stamped);
            Assert.Equal("ms-appdata:///local/b.jpg", inA.AvatarUrl);
            Assert.Equal("ms-appdata:///local/b.jpg", inB.AvatarUrl);
        }

        private static ChatItem BuildGroup(
            string jid,
            GroupMember tip,
            List<GroupMember> decoys)
        {
            var members = new List<GroupMember>(decoys.Count + 1);
            members.AddRange(decoys);
            members.Add(tip);
            return new ChatItem
            {
                JID = jid,
                IsGroup = true,
                GroupMembers = members
            };
        }

        private static List<GroupMember> DecoyMembers(int count)
        {
            var list = new List<GroupMember>(count);
            for (int i = 0; i < count; i++)
            {
                list.Add(new GroupMember
                {
                    Jid = "551190000" + i.ToString("0000") + "@s.whatsapp.net"
                });
            }

            return list;
        }

        private sealed class PassThroughJids : IJidResolver
        {
            public Profile Self => null;

            public string GetCanonicalJid(string jid) => JidHelper.Normalize(jid);
            public string PeekCanonicalJid(string jid) => GetCanonicalJid(jid);

            public bool TryGetAlias(string jid, out string alias)
            {
                alias = null;
                return false;
            }

            public bool IsSelfLinked(string jid) => false;
        }
    }
}
