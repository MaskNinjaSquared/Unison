using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class HistorySqliteChunkAppliedDispatchTests
    {
        [Fact]
        public void On_demand_skips_accumulate_and_finalize()
        {
            var host = new FakeHost();
            HistorySqliteChunkAppliedDispatch.Run(host, "HistorySyncOnDemand", 4);

            Assert.True(host.MarkedReceived);
            Assert.Equal(0, host.Accumulated);
            Assert.False(host.FinalizeCalled);
            Assert.Contains("HistorySyncOnDemand", host.ProgressReason ?? string.Empty);
        }

        [Fact]
        public void Full_accumulates_and_arms_finalize()
        {
            var host = new FakeHost { AwaitingEnrichment = true };
            HistorySqliteChunkAppliedDispatch.Run(host, "Full", 2);

            Assert.Equal(2, host.Accumulated);
            Assert.True(host.FinalizeCalled);
            Assert.True(host.EnrichmentReleased);
            Assert.True(host.UserResyncCompleted);
        }

        private sealed class FakeHost : IHistorySqliteChunkAppliedHost
        {
            public int Accumulated;
            public bool MarkedReceived;
            public bool FinalizeCalled;
            public bool EnrichmentReleased;
            public bool UserResyncCompleted;
            public bool AwaitingEnrichment;
            public string ProgressReason;

            public int SqliteHistoryFullFinalizeDelayMs => 1000;
            public int SqliteHistoryFinalizeQuietMs => 500;
            public bool IsEnrichmentAwaitingHeavyHistory => AwaitingEnrichment;
            public int SqliteHistoryConversationsAccumulated => Accumulated;

            public void MarkHistorySyncReceivedNow() => MarkedReceived = true;

            public void NoteFullHistoryCatchUpProgress(string reason) => ProgressReason = reason;

            public int AddSqliteHistoryConversations(int count)
            {
                Accumulated += count;
                return Accumulated;
            }

            public void PublishInitialSyncProgress(
                bool isActive,
                bool isCompleted,
                int processed,
                int total,
                string stage)
            {
            }

            public int NextSqliteHistoryFinalizeGeneration() => 1;

            public void FinalizeSqliteHistoryAfterQuiet(int generation, int delayMs, int processed) =>
                FinalizeCalled = true;

            public void CompleteUserResyncHistoryWait(string reason) =>
                UserResyncCompleted = true;

            public void RememberLastHistorySyncType(string type)
            {
            }

            public void ReleaseEnrichmentAfterHeavyHistory(string type) =>
                EnrichmentReleased = true;

            public void LogHistorySqliteChunkApplied(string type, int count, int accumulated)
            {
            }
        }
    }
}
