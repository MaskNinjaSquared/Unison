using System;
using System.Collections.Generic;
using System.Linq;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class NameLookupCandidatesTests
    {
        [Fact]
        public void An_address_brings_its_alias_and_both_servers()
        {
            IEnumerable<string> keys = NameLookupCandidates.Expand(
                new[] { "111@s.whatsapp.net" },
                jid => jid,
                jid => jid == "111@s.whatsapp.net" ? "111@lid" : null);

            Assert.Contains("111@s.whatsapp.net", keys);
            Assert.Contains("111@lid", keys);
        }

        [Fact]
        public void A_blank_address_adds_nothing()
        {
            Assert.Empty(NameLookupCandidates.Expand(new[] { " ", null }, jid => jid, jid => null));
        }
    }
}
