using System.Threading.Tasks;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ClearSessionOwnerTests
    {
        [Fact]
        public async Task Run_disconnects_wipes_and_raises_cleared()
        {
            bool captured = false;
            bool disconnected = false;
            bool wiped = false;
            bool raised = false;
            var owner = new ClearSessionOwner(
                log: _ => { },
                captureKeyStore: () => captured = true,
                suppressReconnectToast: () => { },
                clearReconnectToast: () => { },
                clearAuthEarlyAsync: () => Task.CompletedTask,
                nullAuthState: () => { },
                armFatalFlags: () => { },
                stopHealthMonitor: _ => { },
                disconnectSocket: () => disconnected = true,
                resetSessionSignal: () => { },
                raiseSessionClearedAsync: _ =>
                {
                    raised = true;
                    return Task.CompletedTask;
                },
                yieldAsync: () => Task.CompletedTask,
                clearFileKeyStoreAsync: () => Task.CompletedTask,
                clearAuthIdempotentAsync: () => Task.CompletedTask,
                dropSharedFlags: () => { },
                clearNoiseAsync: () => Task.CompletedTask,
                allowPairing: () => { },
                wipeMessagesAsync: () =>
                {
                    wiped = true;
                    return Task.CompletedTask;
                },
                clearRosterAsync: () => Task.CompletedTask,
                clearMemoryAsync: () => Task.CompletedTask,
                clearNotifications: () => { });

            await owner.RunAsync();

            Assert.True(captured);
            Assert.True(disconnected);
            Assert.True(wiped);
            Assert.True(raised);
        }
    }
}
