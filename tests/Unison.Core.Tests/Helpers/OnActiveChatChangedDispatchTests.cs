using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class OnActiveChatChangedDispatchTests
    {
        [Fact]
        public void Unloads_previous_when_changed()
        {
            var host = new FakeHost();
            OnActiveChatChangedDispatch.Run(host, "old@x", "new@x");
            Assert.Equal("old@x", host.Unloaded);
            Assert.True(host.StatusCleared);
        }

        [Fact]
        public void Same_chat_skips_unload_still_clears_status()
        {
            var host = new FakeHost();
            OnActiveChatChangedDispatch.Run(host, "a@x", "a@x");
            Assert.Null(host.Unloaded);
            Assert.True(host.StatusCleared);
        }

        private sealed class FakeHost : IOnActiveChatChangedHost
        {
            public string Unloaded;
            public bool StatusCleared;

            public void UnloadMessageCacheIfInactive(string jid) => Unloaded = jid;

            public void ClearSyncStatus() => StatusCleared = true;
        }
    }
}
