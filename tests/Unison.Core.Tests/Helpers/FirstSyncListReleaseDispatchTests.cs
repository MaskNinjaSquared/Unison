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
        }

        [Fact]
        public void Hold_claims_and_fires_names_pass()
        {
            var host = new FakeHost { HoldInProgress = true };
            FirstSyncListReleaseDispatch.Schedule(host, "stage", 3, 10);

            Assert.True(host.Claimed);
            Assert.True(host.Deferred);
            Assert.True(host.FireAndForgetCalled);
            Assert.False(host.CompletedPublished);
        }

        [Fact]
        public async Task Names_pass_unlocks_and_clears_inflight()
        {
            var host = new FakeHost { HoldInProgress = true, Claimed = true };
            await FirstSyncListReleaseDispatch.RunNamesThenUnlockAsync(host, "stage", 3);

            Assert.True(host.CompletedPublished);
            Assert.True(host.EnrichmentScheduled);
            Assert.False(host.Claimed);
        }

        private sealed class FakeHost : IFirstSyncListReleaseHost
        {
            public bool HoldInProgress;
            public bool Claimed;
            public bool Deferred;
            public bool CompletedPublished;
            public bool FireAndForgetCalled;
            public bool EnrichmentScheduled;

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

            public void MarkFirstSyncListReleaseDeferred() => Deferred = true;

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

            public Task ResolveNamesBeforeListReleaseAsync() => Task.CompletedTask;

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
