// =============================================================================
// GroupMetadataReader — includeRoster:false must not allocate member drafts.
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
    public class GroupMetadataReaderRosterSkipTests
    {
        [Fact]
        public void ReadMetadata_without_roster_keeps_count_but_skips_drafts()
        {
            BinaryNode response = BuildGroupResponse(
                "120363@g.us",
                participantCount: 40,
                sizeAttr: "568");
            var reader = new GroupMetadataReader(new IdentityJids());

            GroupMetadataSnapshot skip = reader.ReadMetadata(response, "120363@g.us", includeRoster: false);
            GroupMetadataSnapshot full = reader.ReadMetadata(response, "120363@g.us", includeRoster: true);

            Assert.NotNull(skip);
            Assert.Equal(568, skip.MemberCount);
            Assert.Empty(skip.Members);

            Assert.NotNull(full);
            Assert.Equal(568, full.MemberCount);
            Assert.Equal(40, full.Members.Count);
        }

        [Fact]
        public void ReadListing_never_materializes_member_drafts()
        {
            BinaryNode group = BuildGroupNode(
                "120363@g.us",
                participantCount: 20,
                sizeAttr: "200");
            var reader = new GroupMetadataReader(new IdentityJids());

            Dictionary<string, GroupMetadataSnapshot> listing =
                reader.ReadListing(new List<BinaryNode> { group });

            Assert.True(listing.TryGetValue("120363@g.us", out GroupMetadataSnapshot snap));
            Assert.Equal(200, snap.MemberCount);
            Assert.Empty(snap.Members);
        }

        private static BinaryNode BuildGroupResponse(string jid, int participantCount, string sizeAttr)
        {
            return new BinaryNode(
                "iq",
                new Dictionary<string, string> { ["type"] = "result" },
                new List<BinaryNode> { BuildGroupNode(jid, participantCount, sizeAttr) });
        }

        private static BinaryNode BuildGroupNode(string jid, int participantCount, string sizeAttr)
        {
            var participants = new List<BinaryNode>(participantCount);
            for (int i = 0; i < participantCount; i++)
            {
                participants.Add(new BinaryNode(
                    "participant",
                    new Dictionary<string, string>
                    {
                        ["jid"] = (1000 + i) + "@s.whatsapp.net"
                    }));
            }

            return new BinaryNode(
                "group",
                new Dictionary<string, string>
                {
                    ["id"] = jid.Replace("@g.us", string.Empty),
                    ["subject"] = "Large Group",
                    ["size"] = sizeAttr
                },
                participants);
        }

        private sealed class IdentityJids : IJidResolver
        {
            public Profile Self => null;

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
