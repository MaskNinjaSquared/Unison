using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ClearSessionDispatchTests
    {
        [Fact]
        public async Task Runs_ordered_wipe_steps()
        {
            var host = new FakeHost();
            await ClearSessionDispatch.RunAsync(host);

            Assert.Equal(
                new[]
                {
                    "capture-keystore",
                    "suppress-toast",
                    "clear-toast",
                    "clear-auth-early",
                    "null-auth",
                    "arm-fatal",
                    "stop-health",
                    "disconnect",
                    "reset-signal",
                    "raise-cleared:False",
                    "yield",
                    "clear-keystore",
                    "clear-auth-idempotent",
                    "drop-shared",
                    "clear-noise",
                    "allow-pairing",
                    "raise-cleared:True",
                    "yield",
                    "wipe-messages",
                    "clear-roster",
                    "clear-memory",
                    "clear-notifications"
                },
                host.Steps);
        }

        private sealed class FakeHost : IClearSessionHost
        {
            public List<string> Steps { get; } = new List<string>();

            public void LogClearSession(string message)
            {
            }

            public void CaptureKeyStoreBeforeDisconnect() => Steps.Add("capture-keystore");

            public void SuppressReconnectToastForClear() => Steps.Add("suppress-toast");

            public void ClearReconnectRequiredToast() => Steps.Add("clear-toast");

            public Task ClearAuthStoreEarlyAsync()
            {
                Steps.Add("clear-auth-early");
                return Task.CompletedTask;
            }

            public void NullAuthState() => Steps.Add("null-auth");

            public void ArmFatalDisconnectFlagsForClear() => Steps.Add("arm-fatal");

            public void StopConnectionHealthMonitor(string reason) => Steps.Add("stop-health");

            public void DisconnectAndDisposeSocket() => Steps.Add("disconnect");

            public void ResetSessionEstablishedSignal() => Steps.Add("reset-signal");

            public Task RaiseSessionClearedAsync(bool startPairing)
            {
                Steps.Add("raise-cleared:" + startPairing);
                return Task.CompletedTask;
            }

            public Task YieldAsync()
            {
                Steps.Add("yield");
                return Task.CompletedTask;
            }

            public Task ClearFileKeyStoreAsync()
            {
                Steps.Add("clear-keystore");
                return Task.CompletedTask;
            }

            public Task ClearAuthStoreIdempotentAsync()
            {
                Steps.Add("clear-auth-idempotent");
                return Task.CompletedTask;
            }

            public void DropSharedKeyStoreAndResumeFlags() => Steps.Add("drop-shared");

            public Task ClearNoiseSessionAsync()
            {
                Steps.Add("clear-noise");
                return Task.CompletedTask;
            }

            public void AllowPairingAfterAuthWipe() => Steps.Add("allow-pairing");

            public Task WipeMessageStoreAsync()
            {
                Steps.Add("wipe-messages");
                return Task.CompletedTask;
            }

            public Task ClearGroupRosterAsync()
            {
                Steps.Add("clear-roster");
                return Task.CompletedTask;
            }

            public Task ClearInMemorySessionStateAsync()
            {
                Steps.Add("clear-memory");
                return Task.CompletedTask;
            }

            public void ClearNotifications() => Steps.Add("clear-notifications");
        }
    }
}
