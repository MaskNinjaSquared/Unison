// =============================================================================
// Tests for MeaningfulChatLabel / GroupIdPlaceholder.
// =============================================================================
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class MeaningfulChatLabelTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Empty_is_not_meaningful(string? label)
        {
            Assert.False(MeaningfulChatLabel.IsMeaningful(label, "5511988887777@s.whatsapp.net", isGroup: false));
        }

        [Fact]
        public void Self_marker_is_never_meaningful()
        {
            Assert.False(MeaningfulChatLabel.IsMeaningful(
                "(You)",
                "5511988887777@s.whatsapp.net",
                isGroup: false,
                isSelfMarker: true));
        }

        [Fact]
        public void A_real_name_is_meaningful()
        {
            Assert.True(MeaningfulChatLabel.IsMeaningful(
                "Ana",
                "5511988887777@s.whatsapp.net",
                isGroup: false));
        }

        [Fact]
        public void A_jid_shaped_label_is_not_meaningful()
        {
            Assert.False(MeaningfulChatLabel.IsMeaningful(
                "5511988887777@s.whatsapp.net",
                "5511988887777@s.whatsapp.net",
                isGroup: false));
        }

        [Fact]
        public void A_phone_echo_of_the_chat_is_not_meaningful()
        {
            Assert.False(MeaningfulChatLabel.IsMeaningful(
                "5511988887777",
                "5511988887777@s.whatsapp.net",
                isGroup: false));
        }

        [Theory]
        [InlineData("393520211245")]
        [InlineData("+39 352 021 1245")]
        public void A_phone_number_on_a_lid_chat_is_not_meaningful(string label)
        {
            // The digits are the paired phone number, so they never echo the LID.
            Assert.False(MeaningfulChatLabel.IsMeaningful(label, "123456789012345@lid", isGroup: false));
        }

        [Fact]
        public void A_short_numeric_nickname_is_meaningful()
        {
            Assert.True(MeaningfulChatLabel.IsMeaningful("007", "123456789012345@lid", isGroup: false));
        }

        [Fact]
        public void A_name_with_an_emoji_flag_is_meaningful()
        {
            Assert.True(MeaningfulChatLabel.IsMeaningful("Leo🇮🇹", "123456789012345@lid", isGroup: false));
        }

        [Fact]
        public void A_resolved_phone_number_does_not_overwrite_a_real_name_on_a_lid_chat()
        {
            var chat = new ChatItem { JID = "123456789012345@lid", Name = "Leo🇮🇹" };

            int changed = ResolvedNameApply.Apply(
                new[] { chat },
                (jid, context) => "393520211245",
                (label, jid, isGroup) => MeaningfulChatLabel.IsMeaningful(label, jid, isGroup),
                membersToo: false);

            Assert.Equal(0, changed);
            Assert.Equal("Leo🇮🇹", chat.Name);
        }

        // Facts, not InlineData: xUnit serializes theory data and turns a lone surrogate into U+FFFD.
        [Fact]
        public void A_name_cut_after_a_high_surrogate_is_not_meaningful()
        {
            Assert.False(MeaningfulChatLabel.IsMeaningful("Leo🇮\uD83C", "123456789012345@lid", isGroup: false));
        }

        [Fact]
        public void A_subject_starting_with_a_low_surrogate_is_not_meaningful()
        {
            Assert.False(MeaningfulChatLabel.IsMeaningful("\uDDF9Family", "120363000000000000@g.us", isGroup: true));
        }

        [Fact]
        public void A_label_cut_through_an_emoji_does_not_overwrite_the_whole_name()
        {
            var chat = new ChatItem { JID = "123456789012345@lid", Name = "Leo🇮🇹" };

            int changed = ResolvedNameApply.Apply(
                new[] { chat },
                (jid, context) => "Leo🇮\uD83C",
                (label, jid, isGroup) => MeaningfulChatLabel.IsMeaningful(label, jid, isGroup),
                membersToo: false);

            Assert.Equal(0, changed);
            Assert.Equal("Leo🇮🇹", chat.Name);
        }

        [Fact]
        public void A_masked_phone_is_not_meaningful()
        {
            Assert.False(MeaningfulChatLabel.IsMeaningful(
                "+55 ••••-7777",
                "5511988887777@s.whatsapp.net",
                isGroup: false));
        }

        [Fact]
        public void A_group_subject_is_meaningful()
        {
            Assert.True(MeaningfulChatLabel.IsMeaningful(
                "Family",
                "120363000000000000@g.us",
                isGroup: true));
        }

        [Fact]
        public void A_group_id_as_subject_is_not_meaningful()
        {
            Assert.False(MeaningfulChatLabel.IsMeaningful(
                "120363000000000000",
                "120363000000000000@g.us",
                isGroup: true));
        }

        [Fact]
        public void Group_Owner_stand_in_is_not_meaningful_for_groups()
        {
            Assert.False(MeaningfulChatLabel.IsMeaningful(
                "Group Owner",
                "120363000000000000@g.us",
                isGroup: true));
        }
    }

    public class GroupIdPlaceholderTests
    {
        [Fact]
        public void Bare_user_part_matching_the_jid_is_a_placeholder()
        {
            Assert.True(GroupIdPlaceholder.IsIdPlaceholder(
                "120363000000000000",
                "120363000000000000@g.us"));
        }

        [Fact]
        public void All_digits_are_a_placeholder()
        {
            Assert.True(GroupIdPlaceholder.IsIdPlaceholder("12345678901", "120363000000000000@g.us"));
        }

        [Fact]
        public void A_named_subject_is_not_a_placeholder()
        {
            Assert.False(GroupIdPlaceholder.IsIdPlaceholder("Family chat", "120363000000000000@g.us"));
        }
    }
}
