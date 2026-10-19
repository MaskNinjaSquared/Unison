using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class GroupNamingTests
    {
        private const string Group = "120363000000000001@g.us";
        private const string Ana = "5511988887777@s.whatsapp.net";
        private const string Me = "5511911112222@s.whatsapp.net";

        private readonly JidAliasTable _aliases = new JidAliasTable();
        private readonly DisplayNameTable _pushNames = new DisplayNameTable();
        private readonly DisplayNameTable _addressBook = new DisplayNameTable();

        private GroupNaming Build()
        {
            _aliases.BindSelf(() => Me, () => null);
            var markers = new FakeMarkers();
            var names = new ContactDisplayNames(_aliases, _pushNames, _addressBook, new RecordingPersonStore(), markers);
            names.Bind(() => new SelfNameInputs { Id = Me }, null);
            return new GroupNaming(names, new AliasJids(_aliases), markers, new FakeStrings());
        }

        [Fact]
        public void A_member_who_is_the_account_reads_as_the_localized_self_name()
        {
            Assert.Equal("Você", Build().ResolveMemberDisplayName(Me));
        }

        [Fact]
        public void A_member_takes_the_address_book_name()
        {
            _addressBook.Set(Ana, "Ana Trabalho");

            Assert.Equal("Ana Trabalho", Build().ResolveMemberDisplayName(Ana));
        }

        [Fact]
        public void An_unknown_member_still_gets_a_label()
        {
            Assert.False(string.IsNullOrWhiteSpace(Build().ResolveMemberDisplayName(Ana)));
        }

        [Fact]
        public void A_group_row_takes_the_cached_subject()
        {
            _pushNames.Set(Group, "Família");

            Assert.Equal("Família", Build().ResolveChatDisplayName(Group));
        }

        [Theory]
        [InlineData("Família", true)]
        [InlineData("120363000000000001", false)]
        [InlineData("(You)", false)]
        [InlineData(" ", false)]
        public void A_group_label_is_meaningful_only_when_it_is_a_real_name(string label, bool expected)
        {
            Assert.Equal(expected, Build().IsMeaningfulGroupLabel(label, Group));
        }

        private sealed class FakeMarkers : ISelfMarkerNaming
        {
            public bool IsMarkerLabel(string label) => label != null && label.Trim() == "(You)";

            public string StripMarker(string label) => IsMarkerLabel(label) ? null : label;
        }

        private sealed class FakeStrings : IStringResources
        {
            public string Get(string key, string fallback = null) =>
                key == "Chat_SelfFallbackName" ? "Você" : fallback;
        }

        private sealed class AliasJids : IJidResolver
        {
            private readonly JidAliasTable _aliases;

            public AliasJids(JidAliasTable aliases) => _aliases = aliases;

            public Profile Self => null;

            public string GetCanonicalJid(string jid) => _aliases.GetCanonicalJid(jid);

            public bool TryGetAlias(string jid, out string alias)
            {
                alias = null;
                return false;
            }

            public bool IsSelfLinked(string jid) => _aliases.IsSelfLinked(jid);
        }
    }
}
