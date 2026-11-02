using System.Collections.Generic;
using Unison.Core.Helpers.Calls;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.ViewModels
{
    public sealed class CallsViewModelFavoritePreviewTests
    {
        [Fact]
        public void SelectOrdered_caps_at_five_and_sets_hasMore()
        {
            var chats = new List<ChatItem>();
            for (int i = 0; i < 6; i++)
            {
                chats.Add(new ChatItem
                {
                    JID = i + "@s.whatsapp.net",
                    Name = "Fav " + i,
                    IsFavorite = true
                });
            }

            bool hasMore;
            IReadOnlyList<ChatItem> preview = CallFavoritePreview.SelectOrdered(
                chats,
                strings: null,
                CallFavoritePreview.DefaultCap,
                showAll: false,
                out hasMore);

            Assert.Equal(5, preview.Count);
            Assert.True(hasMore);
        }

        [Fact]
        public void SelectOrdered_showAll_returns_full_list()
        {
            var chats = new List<ChatItem>();
            for (int i = 0; i < 6; i++)
            {
                chats.Add(new ChatItem
                {
                    JID = i + "@s.whatsapp.net",
                    Name = "Fav " + i,
                    IsFavorite = true
                });
            }

            bool hasMore;
            IReadOnlyList<ChatItem> preview = CallFavoritePreview.SelectOrdered(
                chats,
                strings: null,
                CallFavoritePreview.DefaultCap,
                showAll: true,
                out hasMore);

            Assert.Equal(6, preview.Count);
            Assert.True(hasMore);
        }
    }
}
