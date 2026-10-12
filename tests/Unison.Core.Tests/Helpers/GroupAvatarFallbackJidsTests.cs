using System.Collections.Generic;
using Unison.Baileys.Protocol;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class GroupAvatarFallbackJidsTests
    {
        [Fact]
        public void Parent_and_community_addresses_are_tried_in_wire_order()
        {
            BinaryNode group = Group(
                Child("linked_parent", ("jid", "222@g.us"), ("id", "333")),
                Child("parent", ("jid", "444@g.us")),
                Child("default_sub_group", ("id", "555")),
                Child("default_sub_community", ("jid", "666@g.us")));

            Assert.Equal(
                new[] { "222@g.us", "333@g.us", "444@g.us", "555@g.us", "666@g.us" },
                GroupAvatarFallbackJids.From(group, "111@g.us"));
        }

        [Fact]
        public void The_groups_own_address_is_not_tried_again()
        {
            BinaryNode group = Group(
                Child("linked_parent", ("jid", "111@g.us")),
                Child("parent", ("jid", "111@G.US"), ("id", "222")));

            Assert.Equal(new[] { "222@g.us" }, GroupAvatarFallbackJids.From(group, "111@g.us"));
        }

        [Fact]
        public void A_bare_id_is_completed_and_anything_else_is_dropped()
        {
            Assert.Equal("444@g.us", GroupAvatarFallbackJids.NormalizeGroupJid("  444  "));
            Assert.Equal("555@G.US", GroupAvatarFallbackJids.NormalizeGroupJid("555@G.US"));
            Assert.Null(GroupAvatarFallbackJids.NormalizeGroupJid("55119999@s.whatsapp.net"));
            Assert.Null(GroupAvatarFallbackJids.NormalizeGroupJid("community"));
            Assert.Null(GroupAvatarFallbackJids.NormalizeGroupJid("  "));

            BinaryNode group = Group(Child("parent", ("jid", "55119999@s.whatsapp.net"), ("id", "  444  ")));

            Assert.Equal(new[] { "444@g.us" }, GroupAvatarFallbackJids.From(group, "111@g.us"));
        }

        [Fact]
        public void The_same_address_is_tried_once()
        {
            BinaryNode group = Group(
                Child("linked_parent", ("jid", "222@g.us"), ("id", "222")),
                Child("parent", ("jid", "222@G.US")));

            Assert.Equal(new[] { "222@g.us" }, GroupAvatarFallbackJids.From(group, "111@g.us"));
        }

        [Fact]
        public void No_group_node_tries_nothing()
        {
            Assert.Empty(GroupAvatarFallbackJids.From(null, "111@g.us"));
            Assert.Empty(GroupAvatarFallbackJids.From(Group(), "111@g.us"));
        }

        private static BinaryNode Group(params BinaryNode[] children)
        {
            return new BinaryNode("group", null, new List<BinaryNode>(children));
        }

        private static BinaryNode Child(string tag, params (string Key, string Value)[] attrs)
        {
            var map = new Dictionary<string, string>();
            foreach ((string key, string value) in attrs)
            {
                map[key] = value;
            }

            return new BinaryNode(tag, map);
        }
    }
}
