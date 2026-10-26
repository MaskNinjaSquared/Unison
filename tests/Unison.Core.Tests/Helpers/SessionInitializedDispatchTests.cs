using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class SessionInitializedDispatchTests
    {
        [Fact]
        public async Task Fatal_session_skips_work()
        {
            var host = new FakeHost { FatalSessionEnded = true };

            await SessionInitializedDispatch.RunAsync(host);

            Assert.Equal(
                new[] { "trace:OnSessionInitialized IGNORED (fatal session ended)" },
                host.Log);
        }

        [Fact]
        public async Task Happy_path_schedules_enrichment()
        {
            var host = new FakeHost { DeferReplay = false };

            await SessionInitializedDispatch.RunAsync(host);

            Assert.Contains("mark-established", host.Log);
            Assert.Contains("clear-pairing", host.Log);
            Assert.Contains("enable-pre-session-fatal", host.Log);
            Assert.Contains("reset-streak", host.Log);
            Assert.Contains("complete-wait", host.Log);
            Assert.Contains("ensure-phone", host.Log);
            Assert.Contains("persist-auth", host.Log);
            Assert.Contains("post-replay", host.Log);
            Assert.Contains("force-history:session-initialized", host.Log);
            Assert.Contains("raise-session", host.Log);
            Assert.DoesNotContain(host.Log, line => line.StartsWith("defer:"));
        }

        [Fact]
        public async Task Defer_skips_enrichment()
        {
            var host = new FakeHost { DeferReplay = true };

            await SessionInitializedDispatch.RunAsync(host);

            Assert.Contains("defer:until-replay", host.Log);
            Assert.DoesNotContain("post-replay", host.Log);
            Assert.DoesNotContain(host.Log, line => line.StartsWith("force-history:"));
            Assert.Contains("raise-session", host.Log);
        }

        private sealed class FakeHost : ISessionInitializedHost
        {
            public List<string> Log { get; } = new List<string>();

            public bool FatalSessionEnded { get; set; }

            public bool DeferReplay { get; set; }

            public void MarkSessionEstablished() => Log.Add("mark-established");

            public void ClearPairingRestartPending() => Log.Add("clear-pairing");

            public void EnablePreSessionCloseAsFatal() => Log.Add("enable-pre-session-fatal");

            public void ResetPreSessionCloseStreak() => Log.Add("reset-streak");

            public void CompleteSessionEstablishedWait() => Log.Add("complete-wait");

            public void EnsureSelfPhonePersisted() => Log.Add("ensure-phone");

            public Task PersistAuthStateForSessionAsync()
            {
                Log.Add("persist-auth");
                return Task.CompletedTask;
            }

            public bool ShouldDeferReconnectReplayWork() => DeferReplay;

            public void LogDeferringReconnectWork() =>
                Log.Add("defer:until-replay");

            public void SchedulePostReplayMaintenance() => Log.Add("post-replay");

            public void StartMessageStoreForceHistoryRepair(string reason) =>
                Log.Add("force-history:" + reason);

            public void RaiseSessionInitialized() => Log.Add("raise-session");

            public void PairingTrace(string line) => Log.Add("trace:" + line);

            public void LogSessionInitialized() => Log.Add("log-session");
        }
    }
}
