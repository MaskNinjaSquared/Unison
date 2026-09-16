// =============================================================================
// Tests for PlaceholderChatLabel.
//
// Whether a chat row is still showing a stand-in. Answer it wrong in one
// direction and a conversation stays labelled with a phone number forever;
// wrong in the other and a real name gets overwritten by one.
//
// The five copies this replaced disagreed, so each disagreement is pinned here.
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class PlaceholderChatLabelTests
    {
        private const string Pn = "5511988887777@s.whatsapp.net";
        private const string Lid = "100200300@lid";
        private const string Group = "120363000000000000@g.us";

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void A_row_with_no_label_at_all_is_a_placeholder(string? label)
        {
            // Three of the five copies called Contains("@") without testing this first,
            // so a row with a null name threw instead of being resolved.
            Assert.True(PlaceholderChatLabel.IsPlaceholder(label!, Pn));
        }

        [Theory]
        [InlineData("5511988887777", Pn)]
        [InlineData("100200300", Lid)]
        public void The_bare_number_of_the_rows_own_address_is_a_placeholder(string label, string jid)
        {
            Assert.True(PlaceholderChatLabel.IsPlaceholder(label, jid));
        }

        [Fact]
        public void Any_label_still_carrying_an_address_is_a_placeholder()
        {
            Assert.True(PlaceholderChatLabel.IsPlaceholder(Pn, Pn));
            Assert.True(PlaceholderChatLabel.IsPlaceholder("someone@example.com", Pn));
        }

        [Fact]
        public void The_self_marker_counts_as_unnamed()
        {
            // Two copies left this out, so a row reading "(You)" was treated as named and
            // never sent for resolution -- it stayed that way for the life of the row.
            Assert.True(PlaceholderChatLabel.IsPlaceholder("(You)", Pn, isSelfMarker: true));
            Assert.False(PlaceholderChatLabel.IsPlaceholder("(You)", Pn, isSelfMarker: false));
        }

        [Theory]
        [InlineData("Ana")]
        [InlineData("Ana Souza")]
        [InlineData("5511988887777 Ana")]
        public void A_real_name_is_left_alone(string label)
        {
            Assert.False(PlaceholderChatLabel.IsPlaceholder(label, Pn));
        }

        [Fact]
        public void A_number_belonging_to_a_different_contact_is_a_name_here()
        {
            // Only the row's own address makes its label a placeholder. Someone else's
            // number is odd but it is what the user chose to see, and this rule is not
            // the one that judges that.
            Assert.False(PlaceholderChatLabel.IsPlaceholder("5511999999999", Pn));
        }

        [Fact]
        public void The_bare_number_is_recognised_around_surrounding_space()
        {
            Assert.True(PlaceholderChatLabel.IsPlaceholder("  5511988887777  ", Pn));
        }

        [Fact]
        public void A_group_id_as_a_label_is_a_placeholder_too()
        {
            // The copies that stripped known domains with chained Replace calls missed
            // this: "@g.us" was not on their list, so the comparison never matched.
            Assert.True(PlaceholderChatLabel.IsPlaceholder("120363000000000000", Group));
        }

        [Fact]
        public void With_no_address_to_compare_against_only_the_label_speaks()
        {
            Assert.True(PlaceholderChatLabel.IsPlaceholder(null!, null!));
            Assert.True(PlaceholderChatLabel.IsPlaceholder(Pn, null!));
            Assert.False(PlaceholderChatLabel.IsPlaceholder("Ana", null!));
        }

        // --- BareUser --------------------------------------------------------

        [Theory]
        [InlineData(Pn, "5511988887777")]
        [InlineData(Lid, "100200300")]
        [InlineData(Group, "120363000000000000")]
        [InlineData("5511988887777", "5511988887777")]
        [InlineData("  " + Pn + "  ", "5511988887777")]
        public void The_user_part_is_cut_at_the_separator(string jid, string expected)
        {
            // Cutting at '@' rather than stripping a list of known domains is what makes
            // the group case above work, and any future domain along with it.
            Assert.Equal(expected, PlaceholderChatLabel.BareUser(jid));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void No_address_reduces_to_nothing(string? jid)
        {
            Assert.Equal(string.Empty, PlaceholderChatLabel.BareUser(jid!));
        }
    }
}
