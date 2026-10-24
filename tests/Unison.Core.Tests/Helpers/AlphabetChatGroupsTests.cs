using System.Linq;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class AlphabetChatGroupsTests
    {
        [Fact]
        public void Groups_by_initial_letter_and_omits_empty_buckets()
        {
            var chats = new[]
            {
                new ChatItem { JID = "a@s.whatsapp.net", Name = "Alice" },
                new ChatItem { JID = "b@s.whatsapp.net", Name = "bob" },
                new ChatItem { JID = "z@s.whatsapp.net", Name = "Zoe" },
                new ChatItem { JID = "1@s.whatsapp.net", Name = "123 Shop" }
            };

            var groups = AlphabetChatGroups.Build(chats, c => c.Name);
            Assert.Equal(new[] { "A", "B", "Z", "#" }, groups.Select(g => g.Key).ToArray());
            Assert.Equal("Alice", groups[0][0].Name);
        }
    }
}
