using Unison.Core.Models;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public sealed class SyncWorkloadStrategyTests
    {
        [Fact]
        public void For_maps_each_category_to_its_strategy()
        {
            Assert.Same(UnsupportedSyncWorkload.Instance, SyncWorkloadStrategy.For(DeviceCategory.Unsupported));
            Assert.Same(LowSyncWorkload.Instance, SyncWorkloadStrategy.For(DeviceCategory.Low));
            Assert.Same(HighSyncWorkload.Instance, SyncWorkloadStrategy.For(DeviceCategory.High));
        }

        [Fact]
        public void Unsupported_keeps_the_smallest_memory_footprint()
        {
            SyncWorkloadStrategy s = UnsupportedSyncWorkload.Instance;

            Assert.Equal(50, s.MaxMessagesPerConversation);
            Assert.Equal(80, s.HistoryPersistSubBatchSize);
            Assert.False(s.MaterializeHistoryThumbsDuringSync);
            Assert.Equal(6, s.ChatListReleaseBatchSize);
            Assert.Equal(1, s.AvatarFetchBatchSize);
            Assert.Equal(256u, s.HistoryStorageQuotaMb);
        }

        [Fact]
        public void Low_is_between_unsupported_and_high()
        {
            SyncWorkloadStrategy low = LowSyncWorkload.Instance;
            SyncWorkloadStrategy unsupported = UnsupportedSyncWorkload.Instance;
            SyncWorkloadStrategy high = HighSyncWorkload.Instance;

            Assert.True(low.MaxMessagesPerConversation > unsupported.MaxMessagesPerConversation);
            Assert.True(low.MaxMessagesPerConversation < high.MaxMessagesPerConversation);
            Assert.True(low.MaterializeHistoryThumbsDuringSync);
            Assert.Equal(2, low.AvatarFetchBatchSize);
        }

        [Fact]
        public void High_keeps_the_previous_generous_message_cap()
        {
            Assert.Equal(250, HighSyncWorkload.Instance.MaxMessagesPerConversation);
            Assert.Equal(10240u, HighSyncWorkload.Instance.HistoryStorageQuotaMb);
        }
    }
}
