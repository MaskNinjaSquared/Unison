// =============================================================================
// GroupMetadataReader — the description half of a w:g2 reply.
//
// The wire carries the description as <description id="..."><body>text</body></description>.
// Everything downstream distinguishes "the reply said nothing" (null) from "the group has
// no description" (empty), so these tests pin that distinction at the point it is created.
// =============================================================================
using System.Collections.Generic;
using Unison.Baileys.Protocol;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.WhatsAppClient.Groups;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class GroupMetadataReaderDescriptionTests
    {
        [Fact]
        public void A_description_body_is_read_with_its_wire_id()
        {
            var reader = new GroupMetadataReader(new IdentityJids());

            GroupMetadataSnapshot snap = reader.ReadMetadata(
                Response(Group(Description("Trip planning", "desc-7"))),
                "120363@g.us",
                includeRoster: false);

            Assert.Equal("Trip planning", snap.Description);
            Assert.Equal("desc-7", snap.DescriptionId);
        }

        [Fact]
        public void A_group_with_no_description_node_reports_null_not_empty()
        {
            // Null is what stops a reply that simply did not mention the description from
            // erasing one already stored.
            var reader = new GroupMetadataReader(new IdentityJids());

            GroupMetadataSnapshot snap = reader.ReadMetadata(
                Response(Group()),
                "120363@g.us",
                includeRoster: false);

            Assert.Null(snap.Description);
            Assert.Null(snap.DescriptionId);
        }

        [Fact]
        public void A_description_node_with_no_body_is_an_empty_description()
        {
            // The server sends a bodyless description node when an admin clears it.
            var reader = new GroupMetadataReader(new IdentityJids());

            GroupMetadataSnapshot snap = reader.ReadMetadata(
                Response(Group(new BinaryNode(
                    "description",
                    new Dictionary<string, string> { ["id"] = "desc-8" }))),
                "120363@g.us",
                includeRoster: false);

            Assert.Equal(string.Empty, snap.Description);
            Assert.Equal("desc-8", snap.DescriptionId);
        }

        [Fact]
        public void The_participating_groups_listing_carries_descriptions_too()
        {
            var reader = new GroupMetadataReader(new IdentityJids());

            Dictionary<string, GroupMetadataSnapshot> listing =
                reader.ReadListing(new List<BinaryNode> { Group(Description("Book club", "d1")) });

            Assert.True(listing.TryGetValue("120363@g.us", out GroupMetadataSnapshot snap));
            Assert.Equal("Book club", snap.Description);
        }

        private static BinaryNode Description(string body, string id)
        {
            return new BinaryNode(
                "description",
                new Dictionary<string, string> { ["id"] = id },
                new List<BinaryNode>
                {
                    new BinaryNode("body", null, System.Text.Encoding.UTF8.GetBytes(body))
                });
        }

        private static BinaryNode Group(params BinaryNode[] children)
        {
            return new BinaryNode(
                "group",
                new Dictionary<string, string>
                {
                    ["id"] = "120363",
                    ["subject"] = "A Group"
                },
                new List<BinaryNode>(children));
        }

        private static BinaryNode Response(BinaryNode group)
        {
            return new BinaryNode(
                "iq",
                new Dictionary<string, string> { ["type"] = "result" },
                new List<BinaryNode> { group });
        }

        private sealed class IdentityJids : IJidResolver
        {
            public Profile Self => null;

            public string GetCanonicalJid(string jid) => JidHelper.Normalize(jid) ?? jid;
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
