using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ConnectAsyncDispatchTests
    {
        [Fact]
        public async Task Skip_healthy_does_not_prepare()
        {
            var host = new FakeHost { HealthyRegistered = true };
            await ConnectAsyncDispatch.RunAsync(host);
            Assert.True(host.SkippedHealthy);
            Assert.False(host.Prepared);
            Assert.True(host.LockExited);
        }

        [Fact]
        public async Task Fatal_aborts()
        {
            var host = new FakeHost { Fatal = true };
            await ConnectAsyncDispatch.RunAsync(host);
            Assert.True(host.AbortedFatal);
            Assert.False(host.Prepared);
        }

        [Fact]
        public async Task Proceed_prepares_and_opens()
        {
            var host = new FakeHost();
            await ConnectAsyncDispatch.RunAsync(host);
            Assert.True(host.Prepared);
            Assert.True(host.Opened);
            Assert.True(host.AuthLogged);
        }

        [Fact]
        public async Task Prepare_abort_skips_open()
        {
            var host = new FakeHost
            {
                PrepareResult = ConnectionConnectPrepareResult.AbortSessionWipeInFlight
            };
            await ConnectAsyncDispatch.RunAsync(host);
            Assert.True(host.Prepared);
            Assert.False(host.Opened);
        }

        private sealed class FakeHost : IConnectAsyncHost
        {
            public bool HealthyRegistered;
            public bool Fatal;
            public bool SkippedHealthy;
            public bool AbortedFatal;
            public bool Prepared;
            public bool Opened;
            public bool AuthLogged;
            public bool LockExited;
            public ConnectionConnectPrepareResult PrepareResult =
                ConnectionConnectPrepareResult.Proceed;

            public bool HasHealthyRegisteredSocket => HealthyRegistered;
            public bool ShouldRefuseConnectBecauseSessionDied => Fatal;
            public bool HasHealthyUnregisteredSocket => false;

            public Task EnterConnectLockAsync() => Task.CompletedTask;

            public void ExitConnectLock() => LockExited = true;

            public void WriteConnectSkippedAlreadyHealthy() => SkippedHealthy = true;

            public void WriteConnectAbortedFatal() => AbortedFatal = true;

            public void WriteConnectForceFreshForQr()
            {
            }

            public void TraceForceFreshPairingForQr()
            {
            }

            public Task<ConnectionConnectPrepareResult> PrepareConnectAsync()
            {
                Prepared = true;
                return Task.FromResult(PrepareResult);
            }

            public void WriteConnectPrepareAbort(ConnectionConnectPrepareResult result)
            {
            }

            public void LogConnectAuthState() => AuthLogged = true;

            public bool ComputeReuseLoadedKeyStore() => true;

            public Task OpenConnectAsync(bool reuseLoadedKeyState)
            {
                Opened = true;
                return Task.CompletedTask;
            }
        }
    }
}
