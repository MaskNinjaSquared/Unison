using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class FirstSyncListReleaseDispatchTests
    {
        [Fact]
        public void No_hold_publishes_completed_immediately()
        {
            var host = new FakeHost { HoldInProgress = false };
            FirstSyncListReleaseDispatch.Schedule(host, "done", 5, 5);

            Assert.True(host.CompletedPublished);
            Assert.False(host.FireAndForgetCalled);
            Assert.False(host.NamesResolved);
        }

        [Fact]
        public void Hold_unlocks_immediately_and_fires_names_after()
        {
            var host = new FakeHost { HoldInProgress = true };
            FirstSyncListReleaseDispatch.Schedule(host, "stage", 3, 10);

            Assert.True(host.Claimed);
            Assert.True(host.CompletedPublished);
            Assert.True(host.FireAndForgetCalled);
            Assert.True(host.EnrichmentScheduled);
            Assert.False(host.NamesResolved);
        }

        [Fact]
        public async Task Names_after_unlock_clears_inflight_without_republishing()
        {
            var host = new FakeHost { HoldInProgress = true, Claimed = true, CompletedPublished = true };
            await FirstSyncListReleaseDispatch.RunNamesAfterUnlockAsync(host, "stage");

            Assert.True(host.NamesResolved);
            Assert.False(host.Claimed);
        }

        private sealed class FakeHost : IFirstSyncListReleaseHost
        {
            public bool HoldInProgress;
            public bool Claimed;
            public bool CompletedPublished;
            public bool FireAndForgetCalled;
            public bool EnrichmentScheduled;
            public bool NamesResolved;

            public bool IsFirstSyncListHoldInProgress => HoldInProgress;
            public bool IsFirstSyncUnlockInFlight => Claimed;

            public bool TryClaimFirstSyncUnlockInFlight()
            {
                if (Claimed)
                {
                    return false;
                }

                Claimed = true;
                return true;
            }

            public void MarkFirstSyncListReleaseDeferred()
            {
            }

            public void PublishInitialSyncProgress(
                bool isActive,
                bool isCompleted,
                int processed,
                int total,
                string stage)
            {
                if (isCompleted)
                {
                    CompletedPublished = true;
                }
            }

            public void WriteFirstSyncNamesJournal(string stage, int processed)
            {
            }

            public void LogFirstSyncListRelease(string message)
            {
            }

            public Task ResolveNamesBeforeListReleaseAsync()
            {
                NamesResolved = true;
                return Task.CompletedTask;
            }

            public void RecordFirstSyncNamesException(Exception error, string stage)
            {
            }

            public void ClearSqliteHistoryConversationsAccumulated()
            {
            }

            public void ScheduleDeferredAvatarsAfterUnlock(string stage)
            {
            }

            public void ClearEnrichmentAwaitingHeavyHistory()
            {
            }

            public void TrySchedulePostMessageEnrichment(string reason) =>
                EnrichmentScheduled = true;

            public void ClearFirstSyncUnlockInFlight() => Claimed = false;

            public void FireAndForgetFirstSyncUnlock(Func<Task> work) =>
                FireAndForgetCalled = true;
        }
    }
}
