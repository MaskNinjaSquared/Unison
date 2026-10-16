// =============================================================================
// Tests for UsyncContactCaches.
//
// The two cache touches a usync answer needs that do not involve the socket or
// the account, moved off WhatsAppService (StoreUsyncPushName and
// HasListedChatWithoutAvatar) onto the contact façade:
//
//   - a push name is sanitized against the contact's address and stored under
//     the normalized address; a rejected or blank one stores nothing;
//   - "stored" means a usable name was offered, even if it was already there;
//   - a listed chat without an avatar is a row whose normalized address is the
//     contact's and whose preview url is empty.
// =============================================================================
using System.Collections.Generic;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class UsyncContactCachesTests
    {
        private const string Pn = "5511999990001@s.whatsapp.net";

        private sealed class YouMarker : ISelfMarkerNaming
        {
            public bool IsMarkerLabel(string label) => label?.Trim() == "(You)";

            public string StripMarker(string label) => label;
        }

        private readonly DisplayNameTable _names = new DisplayNameTable();
        private readonly ContactLabelSanitizer _sanitizer = new ContactLabelSanitizer(new YouMarker());

        [Fact]
        public void UsableNameIsStoredUnderNormalizedAddress()
        {
            bool stored = UsyncContactCaches.StorePushName(_names, _sanitizer, "5511999990001:3@s.whatsapp.net", " Ana ", out _);

            Assert.True(stored);
            Assert.True(_names.TryGetValue(Pn, out string name));
            Assert.Equal("Ana", name);
        }

        [Fact]
        public void SameNameAgainStillCountsAsStored()
        {
            _names.Set(Pn, "Ana");

            Assert.True(UsyncContactCaches.StorePushName(_names, _sanitizer, Pn, "Ana", out _));
        }

        [Fact]
        public void SelfMarkerIsRejectedAndReported()
        {
            bool stored = UsyncContactCaches.StorePushName(_names, _sanitizer, Pn, "(You)", out ContactLabelResult result);

            Assert.False(stored);
            Assert.Equal(ContactLabelRejection.SelfMarker, result.Rejection);
            Assert.False(_names.ContainsKey(Pn));
        }

        [Fact]
        public void PhoneEchoIsRejected()
        {
            Assert.False(UsyncContactCaches.StorePushName(_names, _sanitizer, Pn, "+55 11 99999-0001", out _));
            Assert.False(_names.ContainsKey(Pn));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("  ")]
        public void BlankNameOrAddressStoresNothing(string? value)
        {
            Assert.False(UsyncContactCaches.StorePushName(_names, _sanitizer, Pn, value, out _));
            Assert.False(UsyncContactCaches.StorePushName(_names, _sanitizer, value, "Ana", out _));
        }

        [Fact]
        public void ListedChatWithoutAvatarIsFound()
        {
            var chats = new List<ChatItem>
            {
                null,
                new ChatItem { JID = "5511999990001:3@s.whatsapp.net" }
            };

            Assert.True(UsyncContactCaches.HasListedChatWithoutAvatar(chats, Pn));
        }

        [Fact]
        public void ChatWithAvatarOrOtherAddressDoesNotCount()
        {
            var chats = new List<ChatItem>
            {
                new ChatItem { JID = Pn, AvatarUrl = "ms-appdata:///local/a.jpg" },
                new ChatItem { JID = "5511999990002@s.whatsapp.net" }
            };

            Assert.False(UsyncContactCaches.HasListedChatWithoutAvatar(chats, Pn));
        }

        [Fact]
        public void NoChatsOrBlankAddressIsFalse()
        {
            Assert.False(UsyncContactCaches.HasListedChatWithoutAvatar(null, Pn));
            Assert.False(UsyncContactCaches.HasListedChatWithoutAvatar(new[] { new ChatItem { JID = Pn } }, " "));
        }

        [Fact]
        public void RowToFillIsTheFirstListedOneStillWithoutAvatar()
        {
            var withAvatar = new ChatItem { JID = Pn, AvatarUrl = "ms-appdata:///local/a.jpg" };
            var bare = new ChatItem { JID = "5511999990001:3@s.whatsapp.net" };
            var chats = new List<ChatItem> { withAvatar, bare };

            Assert.Same(bare, UsyncContactCaches.FindListedChatWithoutAvatar(chats, Pn));
        }

        [Fact]
        public void NoRowToFillWhenEveryMatchHasAvatar()
        {
            var chats = new List<ChatItem>
            {
                new ChatItem { JID = Pn, AvatarUrl = "ms-appdata:///local/a.jpg" },
                new ChatItem { JID = "5511999990002@s.whatsapp.net" }
            };

            Assert.Null(UsyncContactCaches.FindListedChatWithoutAvatar(chats, Pn));
            Assert.Null(UsyncContactCaches.FindListedChatWithoutAvatar(null, Pn));
        }
    }
}
