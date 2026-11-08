using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.Core.Tests.Helpers;
using Unison.Core.ViewModels;
using Xunit;

namespace Unison.Core.Tests.ViewModels
{
    public sealed class FavoritesRebuildDebounceTests
    {
        [Fact]
        public async Task Name_property_burst_coalesces_into_one_rebuild()
        {
            var dispatcher = new CountingDispatcher();
            var chats = new ChatStateStore(dispatcher);
            var fav = new ChatItem
            {
                JID = "alice@s.whatsapp.net",
                Name = "Alice",
                Kind = ChatKind.Direct,
                IsFavorite = true
            };
            chats.Chats.Add(fav);

            var vm = new FavoritesViewModel(chats, new RecordingChats(), strings: null, dispatcher);
            int rebuilds = 0;
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(FavoritesViewModel.GroupedFavorites))
                {
                    rebuilds++;
                }
            };

            int afterCtor = rebuilds;
            for (int i = 0; i < 15; i++)
            {
                fav.Name = "Alice " + i;
            }

            Assert.Equal(afterCtor, rebuilds);
            Assert.True(SpinWait.SpinUntil(() => rebuilds > afterCtor, TimeSpan.FromSeconds(2)));
            await Task.Delay(150).ConfigureAwait(true);
            Assert.Equal(afterCtor + 1, rebuilds);
        }

        [Fact]
        public void SearchQuery_rebuilds_immediately()
        {
            var dispatcher = new CountingDispatcher();
            var chats = new ChatStateStore(dispatcher);
            chats.Chats.Add(new ChatItem
            {
                JID = "alice@s.whatsapp.net",
                Name = "Alice",
                Kind = ChatKind.Direct,
                IsFavorite = true
            });

            var vm = new FavoritesViewModel(chats, new RecordingChats(), strings: null, dispatcher);
            int rebuilds = 0;
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(FavoritesViewModel.GroupedFavorites))
                {
                    rebuilds++;
                }
            };

            vm.SearchQuery = "Ali";

            Assert.Equal(1, rebuilds);
        }
    }
}
