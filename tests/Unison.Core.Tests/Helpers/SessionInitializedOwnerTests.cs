using System.Threading.Tasks;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class SessionInitializedOwnerTests
    {
        [Fact]
        public async Task Run_marks_established_persists_and_schedules_replay()
        {
            bool established = false;
            bool persisted = false;
            bool postReplay = false;
            bool raised = false;
            var owner = new SessionInitializedOwner(
                fatalSessionEnded: () => false,
                markSessionEstablished: () => established = true,
                clearPairingRestartPending: () => { },
                enablePreSessionCloseAsFatal: () => { },
                resetPreSessionCloseStreak: () => { },
                completeSessionEstablishedWait: () => { },
                ensureSelfPhonePersisted: () => { },
                persistAuthStateAsync: () =>
                {
                    persisted = true;
                    return Task.CompletedTask;
                },
                shouldDeferReconnectReplayWork: () => false,
                logDeferringReconnectWork: () => { },
                schedulePostReplayMaintenance: () => postReplay = true,
                startForceHistoryRepair: _ => { },
                raiseSessionInitialized: () => raised = true,
                pairingTrace: _ => { },
                logSessionInitialized: () => { });

            await owner.RunAsync();

            Assert.True(established);
            Assert.True(persisted);
            Assert.True(postReplay);
            Assert.True(raised);
        }

        [Fact]
        public async Task Fatal_session_skips_work()
        {
            bool established = false;
            var owner = new SessionInitializedOwner(
                fatalSessionEnded: () => true,
                markSessionEstablished: () => established = true,
                clearPairingRestartPending: () => { },
                enablePreSessionCloseAsFatal: () => { },
                resetPreSessionCloseStreak: () => { },
                completeSessionEstablishedWait: () => { },
                ensureSelfPhonePersisted: () => { },
                persistAuthStateAsync: () => Task.CompletedTask,
                shouldDeferReconnectReplayWork: () => false,
                logDeferringReconnectWork: () => { },
                schedulePostReplayMaintenance: () => { },
                startForceHistoryRepair: _ => { },
                raiseSessionInitialized: () => { },
                pairingTrace: _ => { },
                logSessionInitialized: () => { });

            await owner.RunAsync();

            Assert.False(established);
        }
    }
}
