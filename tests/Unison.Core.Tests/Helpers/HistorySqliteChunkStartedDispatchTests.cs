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

        [Fact]
        public void Empty_chunk_after_release_notes_and_bumps_but_skips_publish()
        {
            var host = new FakeHost { AlreadyReleased = true, Accumulated = 12 };
            HistorySqliteChunkStartedDispatch.Run(host, "StatusV3", 0);
            Assert.True(host.Noted);
            Assert.True(host.Bumped);
            Assert.False(host.Published);
        }

        [Fact]
        public void Empty_chunk_before_release_still_publishes()
        {
            var host = new FakeHost { AlreadyReleased = false, Accumulated = 0 };
            HistorySqliteChunkStartedDispatch.Run(host, "StatusV3", 0);
            Assert.True(host.Noted);
            Assert.True(host.Bumped);
            Assert.True(host.Published);
        }

        private sealed class FakeHost : IHistorySqliteChunkStartedHost
        {
            public int Accumulated;
            public bool AlreadyReleased;
            public bool Noted;
            public bool Bumped;
            public bool Published;
            public int PublishedProcessed;

            public int SqliteHistoryConversationsAccumulated => Accumulated;

            public bool FirstSyncListAlreadyReleased => AlreadyReleased;

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
