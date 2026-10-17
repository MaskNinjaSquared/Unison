// =============================================================================
// Tests for PushNameLookup: the first usable push name stored under an address,
// its alias, or the other form of the same user.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class PushNameLookupTests
    {
        private const string Pn = "5511988887777@s.whatsapp.net";
        private const string Lid = "123456789@lid";

        private sealed class NoSelfMarker : ISelfMarkerNaming
        {
            public bool IsMarkerLabel(string label) => false;

            public string StripMarker(string label) => label?.Trim()!;
        }

        private readonly DisplayNameTable _pushNames = new DisplayNameTable();
        private readonly JidAliasTable _aliases = new JidAliasTable();

        private PushNameLookup Lookup(Action<ContactLabelResult, string>? onDecision = null) =>
            new PushNameLookup(_pushNames, _aliases, new ContactLabelSanitizer(new NoSelfMarker()), onDecision!);

        [Fact]
        public void The_name_stored_under_the_address_is_found()
        {
            _pushNames.Set(Pn, "Ana");

            Assert.Equal("Ana", Lookup().FindBest(Pn));
            Assert.True(Lookup().HasName(Pn, Pn));
        }

        [Fact]
        public void A_name_stored_under_the_alias_is_found()
        {
            _aliases.Add(Lid, Pn);
            _pushNames.Set(Pn, "Ana");

            Assert.Equal("Ana", Lookup().FindBest(Lid));
        }

        [Fact]
        public void A_masked_number_is_not_a_name_and_the_decision_is_reported()
        {
            _pushNames.Set(Pn, "+55 ••••-1234");
            var decisions = new List<ContactLabelRejection>();

            string? name = Lookup((result, jid) => decisions.Add(result.Rejection)).FindBest(Pn);

            Assert.Null(name);
            Assert.False(Lookup().HasName(Pn, Pn));
            Assert.Contains(ContactLabelRejection.MaskedPhone, decisions);
        }

        [Fact]
        public void Nothing_stored_finds_nothing()
        {
            Assert.Null(Lookup().FindBest(Pn, null!, " "));
            Assert.False(Lookup().HasName(Pn, null!));
        }
    }
}
