// =============================================================================
// communityCreate node shape — rc14 src/Socket/communities.ts.
// =============================================================================
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unison.Baileys.Protocol;
using Unison.Socket.UseCases.Groups;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class CreateCommunityNodeTests
    {
        [Fact]
        public void Create_node_matches_rc14_children_in_order()
        {
            BinaryNode create = CreateCommunityUseCase.BuildCreateNode("Bairro", "Avisos", "3EB0ABCDEF12");

            Assert.Equal("create", create.Tag);
            Assert.Equal("Bairro", create.GetAttribute("subject"));
            Assert.Null(create.GetAttribute("key"));

            var children = (List<BinaryNode>)create.Content;
            Assert.Equal(
                new[] { "description", "parent", "allow_non_admin_sub_group_creation", "create_general_chat" },
                children.Select(c => c.Tag).ToArray());

            BinaryNode description = children[0];
            Assert.Equal("3EB0ABCDEF12", description.GetAttribute("id"));
            Assert.Equal("Avisos", Encoding.UTF8.GetString(description.GetChild("body").GetContentBytes()));
            Assert.Equal("request_required", children[1].GetAttribute("default_membership_approval_mode"));
        }

        [Fact]
        public void Missing_description_sends_an_empty_body()
        {
            BinaryNode create = CreateCommunityUseCase.BuildCreateNode("Bairro", null, "3EB0ABCDEF12");

            byte[] body = create.GetChild("description").GetChild("body").GetContentBytes();
            Assert.Empty(body);
        }

        [Fact]
        public void Created_jid_is_read_from_the_group_child_and_gets_the_group_server()
        {
            var response = new BinaryNode(
                "iq",
                new Dictionary<string, string> { { "type", "result" } },
                new List<BinaryNode>
                {
                    new BinaryNode("group", new Dictionary<string, string> { { "id", "120363000000000001" } })
                });

            Assert.Equal("120363000000000001@g.us", CreateCommunityUseCase.ReadCreatedJid(response));
        }

        [Fact]
        public void Answer_without_a_group_reads_as_null()
        {
            var response = new BinaryNode("iq", new Dictionary<string, string> { { "type", "result" } });

            Assert.Null(CreateCommunityUseCase.ReadCreatedJid(response));
        }
    }
}
