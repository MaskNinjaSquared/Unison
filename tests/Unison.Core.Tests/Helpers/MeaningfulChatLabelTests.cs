// =============================================================================
// Tests for MeaningfulChatLabel / GroupIdPlaceholder.
// =============================================================================
using Unison.Core.Helpers;
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
