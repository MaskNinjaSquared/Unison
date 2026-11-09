using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class PublishInitialSyncProgressDispatchTests
    {
        [Fact]
        public void Active_progress_owns_list_hold()
        {
            var host = new FakeHost { AlreadyReleased = false };
            PublishInitialSyncProgressDispatch.Run(host, true, false, 1, 0, "s");
            Assert.True(host.HoldOwned);
            Assert.True(host.ChannelPublished);
            Assert.False(host.FlushFired);
            Assert.True(host.LastChannelActive);
        }

        [Fact]
        public void Completed_releases_hold_and_flushes()
        {
            var host = new FakeHost { HoldOwned = true, Deferred = true };
            PublishInitialSyncProgressDispatch.Run(host, true, true, 5, 5, "done");
            Assert.True(host.AlreadyReleased);
            Assert.False(host.HoldOwned);
            Assert.False(host.Deferred);
            Assert.True(host.FlushFired);
        }

        [Fact]
        public void Post_release_active_progress_is_clamped_inactive()
        {
            var host = new FakeHost { AlreadyReleased = true };
            PublishInitialSyncProgressDispatch.Run(host, true, false, 10, 0, "sqlite-started");
            Assert.True(host.ChannelPublished);
            Assert.False(host.LastChannelActive);
            Assert.False(host.HoldOwned);
            Assert.Contains("clamped=post-release", host.LastStage);
            Assert.False(host.FlushFired);
        }

        private sealed class FakeHost : IPublishInitialSyncProgressHost
        {
            public bool AlreadyReleased;
            public bool HoldOwned;
            public bool Deferred;
            public bool ChannelPublished;
            public bool FlushFired;
            public bool LastChannelActive;
            public string LastStage;

            public bool FirstSyncListAlreadyReleased
            {
                get => AlreadyReleased;
                set => AlreadyReleased = value;
            }

            public bool FirstSyncListHoldOwned
            {
                get => HoldOwned;
                set => HoldOwned = value;
            }

            public bool FirstSyncListReleaseDeferred
            {
                get => Deferred;
                set => Deferred = value;
            }

            public void WriteInitialSyncProgressJournal(
                bool active,
                bool completed,
                int processed,
                int total,
                string stage)
            {
                LastChannelActive = active;
                LastStage = stage;
            }

            public void PublishInitialSyncChannel(
                bool active,
                bool completed,
                int processed,
                int total,
                string stage)
            {
                ChannelPublished = true;
                LastChannelActive = active;
                LastStage = stage;
            }

            public void FireAndForgetFlushDiagnosticsOnComplete() => FlushFired = true;
        }
    }
}
