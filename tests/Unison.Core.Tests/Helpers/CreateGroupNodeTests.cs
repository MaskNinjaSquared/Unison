// =============================================================================
// groupCreate node shape — rc14 src/Socket/groups.ts.
// =============================================================================
using System.Collections.Generic;
using System.Linq;
using Unison.Baileys.Protocol;
using Unison.Socket.UseCases.Groups;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class CreateGroupNodeTests
    {
        [Fact]
        public void Create_without_participants_sends_an_empty_create()
        {
            BinaryNode create = CreateGroupUseCase.BuildCreateNode("Só eu", new string[0], "3EB0KEY");

            Assert.Equal("create", create.Tag);
            Assert.Equal("Só eu", create.GetAttribute("subject"));
            Assert.Equal("3EB0KEY", create.GetAttribute("key"));
            var children = create.Content as List<BinaryNode>;
            Assert.True(children == null || children.Count == 0);
        }

        [Fact]
        public void Create_lists_each_participant_by_jid()
        {
            BinaryNode create = CreateGroupUseCase.BuildCreateNode(
                "Turma",
                new[] { "5511999990000@s.whatsapp.net", "123@lid" },
                "3EB0KEY");

            var children = (List<BinaryNode>)create.Content;
            Assert.Equal(new[] { "participant", "participant" }, children.Select(c => c.Tag).ToArray());
            Assert.Equal(
                new[] { "5511999990000@s.whatsapp.net", "123@lid" },
                children.Select(c => c.GetAttribute("jid")).ToArray());
        }
    }
}
