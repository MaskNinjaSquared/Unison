using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class IncomingLiveAnnouncePayloadTests
    {
        [Fact]
        public void From_me_returns_null()
        {
            Assert.Null(IncomingLiveAnnouncePayload.TryCreate(
                chatJid: "x@s.whatsapp.net",
                chatName: "X",
                senderName: null,
                preview: "hi",
                isGroup: false,
                isFromMe: true,
                suppressToast: false,
                quietForSync: false,
                totalUnread: 1,
                isMuted: false,
                avatarUrl: null,
                chatUnread: 1));
        }

        [Fact]
        public void Quiet_for_sync_suppresses_toast_flag()
        {
            IncomingLiveAnnouncePayload payload = IncomingLiveAnnouncePayload.TryCreate(
                chatJid: "x@s.whatsapp.net",
                chatName: "X",
                senderName: "Bob",
                preview: "hi",
                isGroup: false,
                isFromMe: false,
                suppressToast: false,
                quietForSync: true,
                totalUnread: 2,
                isMuted: false,
                avatarUrl: "ms-appdata:///a.jpg",
                chatUnread: 1);

            Assert.NotNull(payload);
            Assert.True(payload.SuppressToast);
            Assert.Equal("x@s.whatsapp.net", payload.ChatJid);
            Assert.Equal(2, payload.TotalUnread);
        }

        [Fact]
        public void From_chat_copies_avatar_unread_and_name_fallback()
        {
            var chat = new ChatItem
            {
                Name = "FromRow",
                AvatarUrl = "ms-appdata:///row.jpg",
                UnreadCount = 4
            };

            IncomingLiveAnnouncePayload payload = IncomingLiveAnnouncePayload.TryCreateFromChat(
                chatJid: "g@g.us",
                chatName: null,
                senderName: "Ann",
                preview: "yo",
                isGroup: true,
                isFromMe: false,
                suppressToast: false,
                quietForSync: false,
                totalUnread: 9,
                chat: chat,
                mutedFallback: true);

            Assert.NotNull(payload);
            Assert.Equal("FromRow", payload.ChatName);
            Assert.Equal("ms-appdata:///row.jpg", payload.AvatarUrl);
            Assert.False(payload.IsMuted);
            Assert.Equal(4, payload.ChatUnread);
            Assert.True(payload.IsGroup);
        }

        [Fact]
        public void Without_chat_uses_muted_fallback()
        {
            IncomingLiveAnnouncePayload payload = IncomingLiveAnnouncePayload.TryCreateFromChat(
                chatJid: "x@s.whatsapp.net",
                chatName: "X",
                senderName: null,
                preview: "hi",
                isGroup: false,
                isFromMe: false,
                suppressToast: false,
                quietForSync: false,
                totalUnread: 1,
                chat: null,
                mutedFallback: true);

            Assert.NotNull(payload);
            Assert.True(payload.IsMuted);
            Assert.Equal(-1, payload.ChatUnread);
        }

        [Fact]
        public void Community_announce_flags_toast_megaphone()
        {
            var chat = new ChatItem
            {
                Name = "WireSubject",
                AvatarUrl = "ms-appdata:///row.jpg",
                UnreadCount = 2,
                IsCommunityAnnounce = true
            };

            IncomingLiveAnnouncePayload payload = IncomingLiveAnnouncePayload.TryCreateFromChat(
                chatJid: "avisos@g.us",
                chatName: null,
                senderName: "Admin",
                preview: "hello",
                isGroup: true,
                isFromMe: false,
                suppressToast: false,
                quietForSync: false,
                totalUnread: 5,
                chat: chat,
                mutedFallback: false);

            Assert.NotNull(payload);
            Assert.True(payload.IsCommunityAnnounce);
            Assert.True(payload.IsGroup);
        }
    }
}
