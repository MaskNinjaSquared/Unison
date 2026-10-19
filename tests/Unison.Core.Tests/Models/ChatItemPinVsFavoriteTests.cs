using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Models
{
    public sealed class ChatItemPinVsFavoriteTests
    {
        [Fact]
        public void IsFavorite_is_not_tied_to_chat_list_pin()
        {
            var chat = new ChatItem
            {
                JID = "1@s.whatsapp.net",
                IsChatPinned = true,
                PinnedTimestamp = 1700000000
            };

            Assert.True(chat.IsChatPinned);
            Assert.False(chat.IsFavorite);
        }
    }
}
