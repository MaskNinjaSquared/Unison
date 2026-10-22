using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Models
{
    public sealed class ChatItemCommunityAnnounceTests
    {
        [Fact]
        public void GetNameResolved_uses_localized_announcements_when_community_announce()
        {
            var chat = new ChatItem
            {
                Kind = ChatKind.Group,
                Name = "ㅤㅤ",
                IsCommunityAnnounce = true
            };

            Assert.Equal("Avisos", chat.GetNameResolved(new StubStrings("Avisos")));
            Assert.Equal("Announcements", chat.GetNameResolved(null));
        }

        [Fact]
        public void GetNameResolved_keeps_group_subject_when_not_community_announce()
        {
            var chat = new ChatItem
            {
                Kind = ChatKind.Group,
                Name = "Família",
                IsCommunityAnnounce = false
            };

            Assert.Equal("Família", chat.GetNameResolved(new StubStrings("Avisos")));
        }

        [Fact]
        public void ChatKind_Community_is_reserved_and_distinct_from_group()
        {
            var chat = new ChatItem { Kind = ChatKind.Community, Name = "Parent" };

            Assert.True(chat.IsCommunity);
            Assert.False(chat.IsGroup);
            Assert.Equal(ChatKind.Community, chat.Kind);
            Assert.Equal("Parent", chat.GetNameResolved(null));
        }

        [Fact]
        public void HistoryChatPreviewApplier_roundtrips_IsCommunityAnnounce()
        {
            var source = new ChatItem
            {
                JID = "120363000000000001@g.us",
                Name = "ㅤㅤ",
                Kind = ChatKind.Group,
                IsCommunityAnnounce = true,
                LastMessage = "hi",
                LastMessageKind = ChatPreviewKind.Text,
                LastMessageTimestampUtc = System.DateTime.UtcNow
            };

            HistoryChatPreview preview = HistoryChatPreviewApplier.FromChatItem(source);
            Assert.NotNull(preview);
            Assert.True(preview.IsCommunityAnnounce);

            ChatItem hydrated = HistoryChatPreviewApplier.ToChatItem(preview);
            Assert.NotNull(hydrated);
            Assert.True(hydrated.IsCommunityAnnounce);
            Assert.Equal(ChatKind.Group, hydrated.Kind);
        }

        private sealed class StubStrings : IStringResources
        {
            private readonly string _announcements;

            public StubStrings(string announcements)
            {
                _announcements = announcements;
            }

            public string Get(string key, string fallback = null)
            {
                if (key == "Chat_CommunityAnnouncements")
                {
                    return _announcements;
                }

                return fallback ?? key;
            }
        }
    }
}
