using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ConnectionConnectPrepareTests
    {
        [Fact]
        public async Task Proceeds_when_auth_ready()
        {
            var host = new FakeHost { HasAuth = true };
            Assert.Equal(
                ConnectionConnectPrepareResult.Proceed,
                await ConnectionConnectPrepare.RunAsync(host));
            Assert.True(host.Applied);
            Assert.True(host.Dropped);
        }

        [Fact]
        public async Task Aborts_when_auth_missing_after_load()
        {
            var host = new FakeHost { HasAuth = false };
            Assert.Equal(
                ConnectionConnectPrepareResult.AbortSessionWipeInFlight,
                await ConnectionConnectPrepare.RunAsync(host));
            Assert.False(host.Applied);
        }

        [Fact]
        public async Task Aborts_when_fatal_after_auth()
        {
            var host = new FakeHost { HasAuth = true, Refuse = true };
            Assert.Equal(
                ConnectionConnectPrepareResult.AbortFatalAfterAuth,
                await ConnectionConnectPrepare.RunAsync(host));
        }

        [Theory]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        [InlineData(false, false, false)]
        public void Pre_session_fatal_policy(bool registered, bool pairingPending, bool expected)
        {
            Assert.Equal(
                expected,
                ConnectionConnectPrepare.CountPreSessionCloseAsFatal(registered, pairingPending));
        }

        [Theory]
        [InlineData(true, 1, 0, true)]
        [InlineData(true, 0, 2, true)]
        [InlineData(true, 0, 0, false)]
        [InlineData(false, 5, 5, false)]
        public void Key_store_reuse(bool hasStore, int sessions, int prekeys, bool expected)
        {
            Assert.Equal(
                expected,
                ConnectionConnectPrepare.ShouldReuseLoadedKeyStore(hasStore, sessions, prekeys));
        }

        private sealed class FakeHost : IConnectionConnectPrepareHost
        {
            public bool HasAuth { get; set; }
            public bool Refuse { get; set; }
            public bool Applied { get; private set; }
            public bool Dropped { get; private set; }

            public bool HasAuthState => HasAuth;
            public bool ShouldRefuseConnectBecauseSessionDied => Refuse;

            public void ResetPerConnectionGates()
            {
            }

            public void MarkConnecting()
            {
            }

            public void StopHealthAndDropCurrentSocket() => Dropped = true;

            public void CancelDeferredAvatars()
            {
            }

            public Task EnsureAuthLoadedAsync() => Task.CompletedTask;

            public void ApplyRegisteredSessionConnectState() => Applied = true;
        }
    }
}
