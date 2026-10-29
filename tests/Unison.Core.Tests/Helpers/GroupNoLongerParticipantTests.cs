// =============================================================================
// Baileys chat.readOnly: a single-group metadata reply that lists participants
// and omits this account, and a 403 on the metadata IQ.
// Listing stays null — a truncated roster is not evidence the account left.
// =============================================================================
using System.Collections.Generic;
using Unison.Baileys.Protocol;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.WhatsAppClient.Groups;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class GroupNoLongerParticipantTests
    {
        private const string Group = "120363000000000001@g.us";
        private const string Self = "5511999999999@s.whatsapp.net";

        [Fact]
        public void ReadMetadata_self_on_the_list_is_still_a_participant()
        {
            var reader = new GroupMetadataReader(new SelfJids(Self));

            GroupMetadataSnapshot snap = reader.ReadMetadata(Response(Self, "other@s.whatsapp.net"), Group);

            Assert.Equal(true, snap.SelfIsParticipant);
        }

        [Fact]
        public void ReadMetadata_self_absent_from_a_listed_roster_left_the_group()
        {
            var reader = new GroupMetadataReader(new SelfJids(Self));

            GroupMetadataSnapshot snap = reader.ReadMetadata(Response("other@s.whatsapp.net"), Group);

            Assert.Equal(false, snap.SelfIsParticipant);
        }

        [Fact]
        public void ReadMetadata_phone_alias_counts_as_self()
        {
            var reader = new GroupMetadataReader(new SelfJids("5511888888888@s.whatsapp.net"));
            BinaryNode response = ResponseWithPhone("lid-user@lid", "5511888888888@s.whatsapp.net");

            GroupMetadataSnapshot snap = reader.ReadMetadata(response, Group);

            Assert.Equal(true, snap.SelfIsParticipant);
        }

        [Fact]
        public void ReadMetadata_without_participants_does_not_guess()
        {
            var reader = new GroupMetadataReader(new SelfJids(Self));
            var response = new BinaryNode(
                "iq",
                new Dictionary<string, string> { ["type"] = "result" },
                new List<BinaryNode>
                {
                    new BinaryNode(
                        "group",
                        new Dictionary<string, string> { ["id"] = "120363000000000001" })
                });

            GroupMetadataSnapshot snap = reader.ReadMetadata(response, Group);

            Assert.Null(snap.SelfIsParticipant);
        }

        [Fact]
        public void ReadListing_does_not_treat_a_missing_self_as_left()
        {
            var reader = new GroupMetadataReader(new SelfJids(Self));
            BinaryNode group = new BinaryNode(
                "group",
                new Dictionary<string, string> { ["id"] = "120363000000000001" },
                new List<BinaryNode>
                {
                    new BinaryNode(
                        "participant",
                        new Dictionary<string, string> { ["jid"] = "other@s.whatsapp.net" })
                });

            Dictionary<string, GroupMetadataSnapshot> listing =
                reader.ReadListing(new List<BinaryNode> { group });

            Assert.True(listing.TryGetValue(Group, out GroupMetadataSnapshot snap));
            Assert.Null(snap.SelfIsParticipant);
        }

        [Fact]
        public void Refusal_403_and_401_mean_no_longer_a_participant()
        {
            Assert.True(GroupMetadataRefusal.IsNoLongerParticipant(Error("403")));
            Assert.True(GroupMetadataRefusal.IsNoLongerParticipant(Error("401")));
            Assert.False(GroupMetadataRefusal.IsNoLongerParticipant(Error("500")));
            Assert.False(GroupMetadataRefusal.IsNoLongerParticipant(null));
        }

        private static BinaryNode Response(params string[] participantJids)
        {
            var children = new List<BinaryNode>(participantJids.Length);
            for (int i = 0; i < participantJids.Length; i++)
            {
                children.Add(new BinaryNode(
                    "participant",
                    new Dictionary<string, string> { ["jid"] = participantJids[i] }));
            }

            return new BinaryNode(
                "iq",
                new Dictionary<string, string> { ["type"] = "result" },
                new List<BinaryNode>
                {
                    new BinaryNode(
                        "group",
                        new Dictionary<string, string> { ["id"] = "120363000000000001" },
                        children)
                });
        }

        private static BinaryNode ResponseWithPhone(string jid, string phone)
        {
            return new BinaryNode(
                "iq",
                new Dictionary<string, string> { ["type"] = "result" },
                new List<BinaryNode>
                {
                    new BinaryNode(
                        "group",
                        new Dictionary<string, string> { ["id"] = "120363000000000001" },
                        new List<BinaryNode>
                        {
                            new BinaryNode(
                                "participant",
                                new Dictionary<string, string>
                                {
                                    ["jid"] = jid,
                                    ["phone_number"] = phone
                                })
                        })
                });
        }

        private static BinaryNode Error(string code)
        {
            return new BinaryNode(
                "iq",
                new Dictionary<string, string> { ["type"] = "error" },
                new List<BinaryNode>
                {
                    new BinaryNode("error", new Dictionary<string, string> { ["code"] = code })
                });
        }

        private sealed class SelfJids : IJidResolver
        {
            private readonly string _self;

            public SelfJids(string self)
            {
                _self = self;
            }

            public Profile Self => null;

            public string GetCanonicalJid(string jid) => JidHelper.Normalize(jid) ?? jid;

            public bool TryGetAlias(string jid, out string alias)
            {
                alias = null;
                return false;
            }

            public bool IsSelfLinked(string jid) =>
                !string.IsNullOrWhiteSpace(jid) &&
                string.Equals(JidHelper.Normalize(jid), JidHelper.Normalize(_self), System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
