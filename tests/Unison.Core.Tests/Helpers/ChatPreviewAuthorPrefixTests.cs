// =============================================================================
// Tests for ChatPreviewNormalizer.FormatListAuthorPrefix — the group strip prefix.
// =============================================================================
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChatPreviewAuthorPrefixTests
    {
        [Fact]
        public void Direct_chats_have_no_author_prefix()
        {
            string prefix = ChatPreviewNormalizer.FormatListAuthorPrefix(
                new ChatMessage { SenderName = "Ana", ParticipantJid = "5511988887777@s.whatsapp.net" },
                isGroup: false);

            Assert.Equal(string.Empty, prefix);
        }

        [Fact]
        public void Own_message_uses_the_localized_self_label()
        {
            string prefix = ChatPreviewNormalizer.FormatListAuthorPrefix(
                new ChatMessage { IsFromMe = true },
                isGroup: true,
                selfDisplayName: "Você");

            Assert.Equal("Você: ", prefix);
        }

        [Fact]
        public void A_known_name_wins()
        {
            string prefix = ChatPreviewNormalizer.FormatListAuthorPrefix(
                new ChatMessage { SenderName = "Ana", ParticipantJid = "199999999999@lid" },
                isGroup: true);

            Assert.Equal("Ana: ", prefix);
        }

        [Fact]
        public void Without_a_name_a_phone_participant_falls_back_to_its_number()
        {
            string prefix = ChatPreviewNormalizer.FormatListAuthorPrefix(
                new ChatMessage { ParticipantJid = "5511988887777@s.whatsapp.net" },
                isGroup: true);

            Assert.Equal("5511988887777: ", prefix);
        }

        /// <summary>
        /// The sync case behind the bug: the chunk names nobody and the envelope carries a LID.
        /// LID digits are not a number, so the strip goes without an author until one is known.
        /// </summary>
        [Fact]
        public void Without_a_name_a_lid_participant_has_no_author()
        {
            string prefix = ChatPreviewNormalizer.FormatListAuthorPrefix(
                new ChatMessage { ParticipantJid = "199999999999@lid" },
                isGroup: true);

            Assert.Equal(string.Empty, prefix);
        }

        [Fact]
        public void Without_a_name_a_lid_participant_uses_the_canonical_phone()
        {
            string prefix = ChatPreviewNormalizer.FormatListAuthorPrefix(
                new ChatMessage { ParticipantJid = "199999999999@lid" },
                isGroup: true,
                selfDisplayName: null,
                canonicalParticipantJid: "5511988887777@s.whatsapp.net");

            Assert.Equal("5511988887777: ", prefix);
        }

        [Fact]
        public void A_self_fallback_name_is_treated_as_no_name()
        {
            string prefix = ChatPreviewNormalizer.FormatListAuthorPrefix(
                new ChatMessage { SenderName = "Me", ParticipantJid = "5511988887777@s.whatsapp.net" },
                isGroup: true);

            Assert.Equal("5511988887777: ", prefix);
        }
    }
}
