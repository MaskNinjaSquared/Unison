using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class OfflineReplayReleaseTests
    {
        [Fact]
        public void Skip_synced_only_on_first_release_without_transport()
        {
            Assert.True(OfflineReplayReleaseGates.ShouldSkipSyncedMark(true, false));
            Assert.False(OfflineReplayReleaseGates.ShouldSkipSyncedMark(true, true));
            Assert.False(OfflineReplayReleaseGates.ShouldSkipSyncedMark(false, false));
        }

        [Fact]
        public void Freshness_log_on_first_release_or_catch_up()
        {
            Assert.True(OfflineReplayReleaseGates.ShouldLogHistoryFreshness(true, false));
            Assert.True(OfflineReplayReleaseGates.ShouldLogHistoryFreshness(false, true));
            Assert.False(OfflineReplayReleaseGates.ShouldLogHistoryFreshness(false, false));
        }

        [Fact]
        public async Task First_release_without_transport_logs_skip_then_notifies()
        {
            var host = new FakeHost
            {
                FirstRelease = true,
                TransportReady = false,
                CatchUpActive = false
            };

            await OfflineReplayRelease.RunAsync(host, offlineCount: 12);

            Assert.Equal(
                new[]
                {
                    "release:12:True",
                    "clear-drain",
                    "flush:offline-complete:12",
                    "skip-synced:12",
                    "notify:12:True:False",
                    "post-replay:12",
                    "freshness:12"
                },
                host.Log);
        }

        [Fact]
        public async Task Catch_up_repeat_release_logs_freshness()
        {
            var host = new FakeHost
            {
                FirstRelease = false,
                TransportReady = true,
                CatchUpActive = true
            };

            await OfflineReplayRelease.RunAsync(host, offlineCount: 3);

            Assert.Equal(
                new[]
                {
                    "release:3:False",
                    "clear-drain",
                    "flush:offline-complete:3",
                    "notify:3:False:True",
                    "post-replay:3",
                    "freshness:3"
                },
                host.Log);
        }

        private sealed class FakeHost : IOfflineReplayReleaseHost
        {
            public List<string> Log { get; } = new List<string>();

            public bool FirstRelease { get; set; }

            public bool TransportReady { get; set; }

            public bool CatchUpActive { get; set; }

            public bool TryMarkFirstRelease() => FirstRelease;

            public void WriteReleaseDiagnostics(int offlineCount, bool firstRelease) =>
                Log.Add("release:" + offlineCount + ":" + firstRelease);

            public void ClearReplayDrainPending() => Log.Add("clear-drain");

            public Task FlushOfflineReplayMessagesAsync(string reason)
            {
                Log.Add("flush:" + reason);
                return Task.CompletedTask;
            }

            public bool IsAssignedTransportReady => TransportReady;

            public void WriteSyncedSkipped(int offlineCount) =>
                Log.Add("skip-synced:" + offlineCount);

            public Task NotifyMessagesReleasedAsync(
                int offlineCount,
                bool firstRelease,
                bool transportReady)
            {
                Log.Add("notify:" + offlineCount + ":" + firstRelease + ":" + transportReady);
                return Task.CompletedTask;
            }

            public void SchedulePostReplayMaintenance(int offlineCount) =>
                Log.Add("post-replay:" + offlineCount);

            public bool IsHistoryCatchUpActive => CatchUpActive;

            public void LogHistoryFreshnessAfterOfflineDrain(int offlineCount) =>
                Log.Add("freshness:" + offlineCount);
        }
    }
}
