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
    public sealed class CallsFavoritesRebuildDebounceTests
    {
        [Fact]
        public async Task Name_property_burst_coalesces_favorite_preview_rebuild()
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

            var vm = new CallsViewModel(chats, new RecordingChats(), strings: null, dispatcher);
            int rebuilds = 0;
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(CallsViewModel.FavoritePreview))
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
    }
}
