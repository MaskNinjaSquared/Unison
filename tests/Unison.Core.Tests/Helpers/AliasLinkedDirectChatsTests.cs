using System;
using System.Collections.Generic;
using System.Linq;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class AliasLinkedDirectChatsTests
    {
        [Fact]
        public void A_direct_address_is_kept_with_its_canonical_and_a_group_is_not()
        {
            IReadOnlyList<string> chats = AliasLinkedDirectChats.Collect(
                new[] { "111@s.whatsapp.net", "999@g.us", "  " },
                jid => jid,
                jid => jid == "111@s.whatsapp.net" ? "111-canon@s.whatsapp.net" : jid);

            Assert.Contains("111@s.whatsapp.net", chats);
            Assert.Contains("111-canon@s.whatsapp.net", chats);
            Assert.DoesNotContain("999@g.us", chats);
        }

        [Fact]
        public void The_same_address_in_another_case_is_one_entry()
        {
            IReadOnlyList<string> chats = AliasLinkedDirectChats.Collect(
                new[] { "111@s.whatsapp.net", "111@S.WHATSAPP.NET" },
                jid => jid,
                jid => jid);

            Assert.Single(chats);
        }
    }
}
