// =============================================================================
// ComposeAuthor / ToChatItem — strip must survive cold start when only a LID is stored.
// =============================================================================
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class HistoryChatPreviewComposeAuthorTests
    {
        [Fact]
        public void ToChatItem_keeps_a_persisted_author_when_recompose_has_only_a_lid()
        {
            var preview = new HistoryChatPreview
            {
                Jid = "120363000000000000@g.us",
                Name = "Família",
                IsGroup = true,
                LastMessage = "oi",
                LastMessageKind = ChatPreviewKind.Text,
                LastMessageTimestampUtc = System.DateTime.UtcNow,
                LastMessageParticipantJid = "199999999999@lid",
                LastMessageSenderName = null,
                LastMessageAuthor = "Ana: "
            };

            ChatItem chat = HistoryChatPreviewApplier.ToChatItem(preview, selfDisplayName: "Você");

            Assert.NotNull(chat);
            Assert.Equal("Ana: ", chat.LastMessageAuthor);
        }

        [Fact]
        public void ToChatItem_uses_a_usable_sender_name_over_the_persisted_prefix()
        {
            var preview = new HistoryChatPreview
            {
                Jid = "120363000000000000@g.us",
                Name = "Família",
                IsGroup = true,
                LastMessage = "oi",
                LastMessageKind = ChatPreviewKind.Text,
                LastMessageTimestampUtc = System.DateTime.UtcNow,
                LastMessageParticipantJid = "5511988887777@s.whatsapp.net",
                LastMessageSenderName = "Maria",
                LastMessageAuthor = "stale: "
            };

            ChatItem chat = HistoryChatPreviewApplier.ToChatItem(preview, selfDisplayName: "Você");

            Assert.NotNull(chat);
            Assert.Equal("Maria: ", chat.LastMessageAuthor);
        }

        [Fact]
        public void ToChatItem_falls_back_to_the_phone_when_no_name_and_no_persisted_author()
        {
            var preview = new HistoryChatPreview
            {
                Jid = "120363000000000000@g.us",
                Name = "Família",
                IsGroup = true,
                LastMessage = "oi",
                LastMessageKind = ChatPreviewKind.Text,
                LastMessageTimestampUtc = System.DateTime.UtcNow,
                LastMessageParticipantJid = "5511988887777@s.whatsapp.net",
                LastMessageSenderName = null,
                LastMessageAuthor = null
            };

            ChatItem chat = HistoryChatPreviewApplier.ToChatItem(preview, selfDisplayName: "Você");

            Assert.NotNull(chat);
            Assert.Equal("5511988887777: ", chat.LastMessageAuthor);
        }

        [Fact]
        public void ToChatItem_drops_a_strip_that_was_the_group_title()
        {
            var preview = new HistoryChatPreview
            {
                Jid = "120363000000000000@g.us",
                Name = "Família",
                IsGroup = true,
                LastMessage = "oi",
                LastMessageKind = ChatPreviewKind.Text,
                LastMessageTimestampUtc = System.DateTime.UtcNow,
                LastMessageParticipantJid = "199999999999@lid",
                LastMessageSenderName = "Família",
                LastMessageAuthor = "Família: "
            };

            ChatItem chat = HistoryChatPreviewApplier.ToChatItem(preview, selfDisplayName: "Você");

            Assert.NotNull(chat);
            Assert.Equal(string.Empty, chat.LastMessageAuthor);
        }
    }
}
