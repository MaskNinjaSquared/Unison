using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class LoadPersistedUiStateDispatchTests
    {
        [Fact]
        public async Task Unregistered_skips_load()
        {
            var host = new FakeHost { Registered = false };
            await LoadPersistedUiStateDispatch.RunAsync(host);
            Assert.True(host.Initialized);
            Assert.False(host.ChatsLoaded);
        }

        [Fact]
        public async Task Already_loaded_skips()
        {
            var host = new FakeHost { Loaded = true, Registered = true };
            await LoadPersistedUiStateDispatch.RunAsync(host);
            Assert.False(host.ChatsLoaded);
        }

        [Fact]
        public async Task Loads_hydrates_and_reconciles()
        {
            var host = new FakeHost { Registered = true, Rows = 3 };
            await LoadPersistedUiStateDispatch.RunAsync(host);
            Assert.True(host.ChatsLoaded);
            Assert.True(host.Loaded);
            Assert.True(host.Hydrated);
            Assert.True(host.Reconciled);
            Assert.Equal(3, host.JournalRows);
        }

        private sealed class FakeHost : ILoadPersistedUiStateHost
        {
            public bool Registered;
            public bool Loaded;
            public bool Initialized;
            public bool ChatsLoaded;
            public bool Hydrated;
            public bool Reconciled;
            public int Rows;
            public int JournalRows;

            public bool PersistedUiStateLoaded => Loaded;
            public bool IsAuthRegistered => Registered;
            public int ChatRowCount => Rows;

            public Task InitializeConnectionStateAsync()
            {
                Initialized = true;
                return Task.CompletedTask;
            }

            public Task EnterPersistedUiLoadLockAsync() => Task.CompletedTask;

            public void ExitPersistedUiLoadLock()
            {
            }

            public Task LoadPersistedChatsAsync()
            {
                ChatsLoaded = true;
                return Task.CompletedTask;
            }

            public void MarkPersistedUiStateLoaded() => Loaded = true;

            public void WritePersistedUiLoadedJournal(int chatRowCount) =>
                JournalRows = chatRowCount;

            public Task HydrateCachedAvatarsBestEffortAsync()
            {
                Hydrated = true;
                return Task.CompletedTask;
            }

            public Task ReconcileChatPreviewsBestEffortAsync()
            {
                Reconciled = true;
                return Task.CompletedTask;
            }
        }
    }
}
