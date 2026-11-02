using System.Collections.Generic;
using Unison.Baileys.Protocol;
using Unison.Socket.UseCases.Groups;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class CommunityLinkedGroupsParseTests
    {
        [Fact]
        public void ParseSubGroups_reads_id_subject_owner_size_creation()
        {
            var subGroups = new BinaryNode(
                "sub_groups",
                null,
                new List<BinaryNode>
                {
                    new BinaryNode(
                        "group",
                        new Dictionary<string, string>
                        {
                            ["id"] = "120363111111111111",
                            ["subject"] = "Geral",
                            ["creation"] = "1700000000",
                            ["creator"] = "5511999999999",
                            ["size"] = "42"
                        })
                });

            IList<CommunityLinkedGroupInfo> parsed =
                FetchCommunityLinkedGroupsUseCase.ParseSubGroups(subGroups);

            Assert.Single(parsed);
            Assert.Equal("120363111111111111@g.us", parsed[0].Jid);
            Assert.Equal("Geral", parsed[0].Subject);
            Assert.Equal(1700000000L, parsed[0].CreationUnixSeconds);
            Assert.Equal("5511999999999@s.whatsapp.net", parsed[0].Owner);
            Assert.Equal(42, parsed[0].Size);
        }

        [Fact]
        public void ParseSubGroups_skips_nodes_without_id()
        {
            var subGroups = new BinaryNode(
                "sub_groups",
                null,
                new List<BinaryNode>
                {
                    new BinaryNode(
                        "group",
                        new Dictionary<string, string> { ["subject"] = "NoId" })
                });

            IList<CommunityLinkedGroupInfo> parsed =
                FetchCommunityLinkedGroupsUseCase.ParseSubGroups(subGroups);

            Assert.Empty(parsed);
        }
    }
}
