using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Unison.WhatsAppClient.History;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class HistoryHydrationBackfillOwnerTests
    {
        [Fact]
        public async Task StartAsync_acquires_lock_and_runs_io_core()
        {
            var io = new FakeIo { CandidateCount = 0 };
            var owner = new HistoryHydrationBackfill(io);

            int n = await owner.StartAsync("owner-test");

            Assert.Equal(0, n);
            Assert.False(owner.IsActive);
            Assert.True(io.CoreRan);
        }

        [Fact]
        public async Task StartAsync_sets_IsActive_false_when_finished()
        {
            var io = new FakeIo { CandidateCount = 0 };
            var owner = new HistoryHydrationBackfill(io);

            Assert.False(owner.IsActive);
            await owner.StartAsync("done");
            Assert.False(owner.IsActive);
            Assert.True(io.CoreRan);
        }

        private sealed class FakeIo : IHistoryHydrationBackfillHost
        {
            public int CandidateCount;
            public bool CoreRan;

            public bool IsConnected => true;
            public int HydrationBatchSize => 10;
            public int HydrationPageSize => 50;
            public int HydrationMaxPagesPerChat => 3;

            public System.Collections.Generic.IReadOnlyList<HistoryBackfillCandidate>
                BuildHydrationCandidates(System.DateTime nowUtc)
            {
                CoreRan = true;
                return CandidateCount == 0
                    ? System.Array.Empty<HistoryBackfillCandidate>()
                    : new[] { new HistoryBackfillCandidate { Jid = "a@g.us" } };
            }

            public int GetHydrationMessageCount(string jid) => 0;
            public string NormalizeHydrationJid(string jid) => jid;
            public Task<bool> RequestHydrationOnDemandAsync(
                string normJid, int pageSize, string triggerReason) =>
                Task.FromResult(false);
            public Task WaitForHydrationSettleAsync() => Task.CompletedTask;
            public void LogHydrationSkippedNotConnected(string reason) { }
            public void LogHydrationNoCandidates(string reason) { }
            public void WriteHydrationStart(
                string reason, int chatCount, int batchSize, int maxPages) { }
            public void WriteHydrationBatch(
                int batchIndex,
                int sentCount,
                int ramGrew,
                int hasMoreCount,
                HistoryBackfillContinueAction action) { }
            public void LogHydrationDone(
                int requested, int synced, int hasMoreLeft, int failed) { }
        }
    }
}
