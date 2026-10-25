// =============================================================================
// ChatAuthorStripMaps — direct-chat + roster indexes for the list author strip.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChatAuthorStripMapsTests
    {
        [Fact]
        public void BuildDirectChatNames_indexes_1to1_by_raw_and_canonical()
        {
            var jids = new AliasJids();
            jids.Pair("1999@lid", "5511988887777@s.whatsapp.net");

            var chats = new List<ChatItem>
            {
                new ChatItem
                {
                    JID = "1999@lid",
                    Name = "Ana Souza",
                    IsGroup = false
                },
                new ChatItem
                {
                    JID = "120363@g.us",
                    Name = "Família",
                    IsGroup = true
                }
            };

            Dictionary<string, string> map = ChatAuthorStripMaps.BuildDirectChatNames(chats, jids);

            Assert.Equal("Ana Souza", map["1999@lid"]);
            Assert.Equal("Ana Souza", map["5511988887777@s.whatsapp.net"]);
            Assert.False(map.ContainsKey("120363@g.us"));
        }

        [Fact]
        public void FindRosterTip_returns_matching_member_without_full_index()
        {
            var jids = new AliasJids();
            jids.Pair("1999@lid", "5511988887777@s.whatsapp.net");

            var tip = new GroupMember
            {
                Jid = "1999@lid",
                DisplayName = "Ana Tip"
            };
            var group = new ChatItem
            {
                JID = "120363@g.us",
                IsGroup = true,
                GroupMembers = new List<GroupMember>
                {
                    new GroupMember { Jid = "other@lid", DisplayName = "Other" },
                    tip
                }
            };

            Assert.Same(
                tip,
                ChatAuthorStripMaps.FindRosterTip(group, "5511988887777@s.whatsapp.net", jids));
        }

        [Fact]
        public void TryGetDirectName_hits_canonical_key()
        {
            var jids = new AliasJids();
            jids.Pair("1999@lid", "5511988887777@s.whatsapp.net");
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["5511988887777@s.whatsapp.net"] = "Ana Souza"
            };

            string name;
            Assert.True(ChatAuthorStripMaps.TryGetDirectName(map, "1999@lid", jids, out name));
            Assert.Equal("Ana Souza", name);
        }

        [Fact]
        public void BuildRosterByCanonical_indexes_lid_and_phone()
        {
            var jids = new AliasJids();
            jids.Pair("1999@lid", "5511988887777@s.whatsapp.net");

            var member = new GroupMember
            {
                Jid = "1999@lid",
                Lid = "1999@lid",
                PhoneNumber = "5511988887777",
                DisplayName = "Ana Roster"
            };
            var group = new ChatItem
            {
                JID = "120363@g.us",
                IsGroup = true,
                GroupMembers = new List<GroupMember> { member }
            };

            Dictionary<string, GroupMember> roster =
                ChatAuthorStripMaps.BuildRosterByCanonical(group, jids);

            Assert.Same(member, roster["1999@lid"]);
            Assert.Same(member, roster["5511988887777@s.whatsapp.net"]);
        }

        private sealed class AliasJids : IJidResolver
        {
            private readonly Dictionary<string, string> _pairs =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public Profile Self => null;

            public void Pair(string left, string right)
            {
                string a = JidHelper.Normalize(left);
                string b = JidHelper.Normalize(right);
                _pairs[a] = b;
                _pairs[b] = a;
            }

            public string GetCanonicalJid(string jid)
            {
                string key = JidHelper.Normalize(jid);
                string alias;
                if (key != null && _pairs.TryGetValue(key, out alias) && JidHelper.IsPhoneJid(alias))
                {
                    return alias;
                }

                return key;
            }

            public bool TryGetAlias(string jid, out string alias)
            {
                string key = JidHelper.Normalize(jid);
                if (key != null && _pairs.TryGetValue(key, out alias))
                {
                    return true;
                }

                alias = null;
                return false;
            }

            public bool IsSelfLinked(string jid) => false;
        }
    }
}
