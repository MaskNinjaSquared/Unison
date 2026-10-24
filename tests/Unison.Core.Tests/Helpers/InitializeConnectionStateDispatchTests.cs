using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class InitializeConnectionStateDispatchTests
    {
        [Fact]
        public async Task Skips_when_auth_already_loaded()
        {
            var host = new FakeHost { HasAuth = true };
            await InitializeConnectionStateDispatch.RunAsync(host);
            Assert.False(host.StoreInitialized);
        }

        [Fact]
        public async Task Runs_steps_in_order_when_cold()
        {
            var host = new FakeHost();
            await InitializeConnectionStateDispatch.RunAsync(host);
            Assert.Equal(
                "store,freshness,journal,auth,toast,lid,aliases,self,profile",
                host.Trace);
        }

        private sealed class FakeHost : IInitializeConnectionStateHost
        {
            public bool HasAuth;
            public bool StoreInitialized;
            public string Trace = string.Empty;

            public bool HasAuthState => HasAuth;

            private void Step(string name) =>
                Trace = string.IsNullOrEmpty(Trace) ? name : Trace + "," + name;

            public Task InitializeMessageStoreAsync()
            {
                StoreInitialized = true;
                Step("store");
                return Task.CompletedTask;
            }

            public void LoadHistoryFreshnessRepairState() => Step("freshness");

            public Task RecoverPendingIncomingJournalAsync()
            {
                Step("journal");
                return Task.CompletedTask;
            }

            public Task LoadOrCreateAuthStateAsync()
            {
                Step("auth");
                return Task.CompletedTask;
            }

            public void ApplyLinkedAccountReconnectToastGate() => Step("toast");

            public Task WarmLidMappingCacheAsync()
            {
                Step("lid");
                return Task.CompletedTask;
            }

            public Task RestorePersistedJidAliasesAsync()
            {
                Step("aliases");
                return Task.CompletedTask;
            }

            public void RegisterSelfIdentitySocketAlias() => Step("self");

            public void SyncSelfProfileFromAuth() => Step("profile");
        }
    }
}
