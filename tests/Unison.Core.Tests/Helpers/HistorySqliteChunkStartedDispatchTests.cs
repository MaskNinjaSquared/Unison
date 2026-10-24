using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class HistorySqliteChunkStartedDispatchTests
    {
        [Fact]
        public void On_demand_notes_progress_only()
        {
            var host = new FakeHost();
            HistorySqliteChunkStartedDispatch.Run(host, "HistorySyncOnDemand", 2);
            Assert.True(host.Noted);
            Assert.False(host.Bumped);
            Assert.False(host.Published);
        }

        [Fact]
        public void Bootstrap_bumps_generation_and_publishes()
        {
            var host = new FakeHost { Accumulated = 5 };
            HistorySqliteChunkStartedDispatch.Run(host, "Bootstrap", 3);
            Assert.True(host.Noted);
            Assert.True(host.Bumped);
            Assert.True(host.Published);
            Assert.Equal(5, host.PublishedProcessed);
        }

        private sealed class FakeHost : IHistorySqliteChunkStartedHost
        {
            public int Accumulated;
            public bool Noted;
            public bool Bumped;
            public bool Published;
            public int PublishedProcessed;

            public int SqliteHistoryConversationsAccumulated => Accumulated;

            public void NoteFullHistoryCatchUpProgress(string reason) => Noted = true;

            public void BumpSqliteHistoryFinalizeGeneration() => Bumped = true;

            public void PublishInitialSyncProgress(
                bool isActive,
                bool isCompleted,
                int processed,
                int total,
                string stage)
            {
                Published = true;
                PublishedProcessed = processed;
            }
        }
    }
}
