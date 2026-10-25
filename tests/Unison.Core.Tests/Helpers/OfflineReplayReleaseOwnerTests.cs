using System.Threading.Tasks;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class OfflineReplayReleaseOwnerTests
    {
        [Fact]
        public async Task Run_flushes_notifies_and_schedules_post_replay()
        {
            bool flushed = false;
            bool notified = false;
            bool scheduled = false;
            var owner = new OfflineReplayReleaseOwner(
                tryMarkFirstRelease: () => true,
                writeReleaseDiagnostics: (_, __) => { },
                clearReplayDrainPending: () => { },
                flushOfflineReplay: _ =>
                {
                    flushed = true;
                    return Task.CompletedTask;
                },
                isAssignedTransportReady: () => true,
                writeSyncedSkipped: _ => { },
                notifyMessagesReleased: (count, first, ready) =>
                {
                    notified = first && ready && count == 3;
                    return Task.CompletedTask;
                },
                schedulePostReplayMaintenance: count => scheduled = count == 3,
                isHistoryCatchUpActive: () => false,
                logHistoryFreshness: _ => { });

            await owner.RunAsync(3);

            Assert.True(flushed);
            Assert.True(notified);
            Assert.True(scheduled);
        }
    }
}
