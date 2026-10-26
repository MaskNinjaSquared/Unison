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

        private sealed class FakeHost : IPublishInitialSyncProgressHost
        {
            public bool AlreadyReleased;
            public bool HoldOwned;
            public bool Deferred;
            public bool ChannelPublished;
            public bool FlushFired;

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
            }

            public void PublishInitialSyncChannel(
                bool active,
                bool completed,
                int processed,
                int total,
                string stage) =>
                ChannelPublished = true;

            public void FireAndForgetFlushDiagnosticsOnComplete() => FlushFired = true;
        }
    }
}
