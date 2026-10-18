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
            Assert.Equal(0, s.MaxFullHistoryCatchUpRounds);
            Assert.Equal(6000, s.HistoryFinalizeQuietMs);
            Assert.Equal(2000, s.HistoryFullFinalizeDelayMs);
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
            // Low / Mobile: first quiet lot only (same as Unsupported) — adjacent pages OOMed Creators.
            Assert.Equal(0, low.MaxFullHistoryCatchUpRounds);
            Assert.Equal(0, unsupported.MaxFullHistoryCatchUpRounds);
            Assert.True(high.MaxFullHistoryCatchUpRounds > 0);
            Assert.True(low.HistoryFinalizeQuietMs < unsupported.HistoryFinalizeQuietMs);
            Assert.True(low.HistoryFinalizeQuietMs > high.HistoryFinalizeQuietMs);
        }

        [Fact]
        public void High_keeps_the_previous_generous_defaults()
        {
            SyncWorkloadStrategy s = HighSyncWorkload.Instance;

            Assert.Equal(250, s.MaxMessagesPerConversation);
            Assert.Equal(10240u, s.HistoryStorageQuotaMb);
            Assert.False(s.SerializePostSyncEnrichment);
            Assert.Equal(60, s.MaxFullHistoryCatchUpRounds);
            Assert.Equal(2800, s.HistoryFinalizeQuietMs);
            Assert.Equal(900, s.HistoryFullFinalizeDelayMs);
        }

        [Fact]
        public void Unsupported_and_Low_serialize_post_sync_enrichment()
        {
            Assert.True(UnsupportedSyncWorkload.Instance.SerializePostSyncEnrichment);
            Assert.True(LowSyncWorkload.Instance.SerializePostSyncEnrichment);
        }

        [Fact]
        public void High_keeps_the_previous_group_batch_numbers()
        {
            SyncWorkloadStrategy s = HighSyncWorkload.Instance;

            Assert.Equal(25, s.GroupMetadataBatchSize);
            Assert.Equal(120, s.GroupMetadataPauseMs);
            Assert.Equal(16, s.GroupRosterBatchSize);
            Assert.Equal(900, s.GroupRosterInterRequestDelayMs);
            Assert.Equal(20, s.GroupRosterNextBatchDelaySeconds);
        }

        [Fact]
        public void Group_work_shrinks_with_the_device()
        {
            SyncWorkloadStrategy unsupported = UnsupportedSyncWorkload.Instance;
            SyncWorkloadStrategy low = LowSyncWorkload.Instance;
            SyncWorkloadStrategy high = HighSyncWorkload.Instance;

            Assert.True(unsupported.GroupMetadataBatchSize < low.GroupMetadataBatchSize);
            Assert.True(low.GroupMetadataBatchSize < high.GroupMetadataBatchSize);
            Assert.True(unsupported.GroupMetadataPauseMs > low.GroupMetadataPauseMs);
            Assert.True(low.GroupMetadataPauseMs > high.GroupMetadataPauseMs);
            Assert.True(unsupported.GroupRosterBatchSize < low.GroupRosterBatchSize);
            Assert.True(low.GroupRosterBatchSize < high.GroupRosterBatchSize);
            Assert.True(unsupported.GroupRosterNextBatchDelaySeconds > high.GroupRosterNextBatchDelaySeconds);
        }

        [Theory]
        [InlineData(true, 3000, 6000, 45, 3)]
        [InlineData(false, 1000, 1800, 35, 2)]
        public void High_and_Low_keep_the_previous_frugal_and_generous_pacing(
            bool frugal,
            int startupQuietMs,
            int deferredStartupMs,
            int catchUpIdleAbortSeconds,
            int stagnantCycles)
        {
            foreach (SyncWorkloadStrategy s in new SyncWorkloadStrategy[] { HighSyncWorkload.Instance, LowSyncWorkload.Instance })
            {
                SyncPacing pacing = s.PacingFor(frugal);
                Assert.Equal(startupQuietMs, pacing.StartupQuietFloorMs);
                Assert.Equal(deferredStartupMs, pacing.DeferredStartupQuietMs);
                Assert.Equal(catchUpIdleAbortSeconds, pacing.CatchUpIdleAbortSeconds);
                Assert.Equal(stagnantCycles, pacing.CatchUpStagnantCyclesBeforeStop);
            }
        }

        [Fact]
        public void Unsupported_waits_longer_when_frugal()
        {
            SyncPacing unsupported = UnsupportedSyncWorkload.Instance.PacingFor(frugal: true);
            SyncPacing high = HighSyncWorkload.Instance.PacingFor(frugal: true);

            Assert.True(unsupported.StartupQuietFloorMs > high.StartupQuietFloorMs);
            Assert.True(unsupported.DeferredStartupQuietMs > high.DeferredStartupQuietMs);
        }

        [Fact]
        public void Unsupported_and_Low_skip_adjacent_full_history_pages()
        {
            // Cap 0: first quiet lot finishes with FinishMaxContinues — no soft-reconnect cascade.
            Assert.Equal(0, UnsupportedSyncWorkload.Instance.MaxFullHistoryCatchUpRounds);
            Assert.Equal(0, LowSyncWorkload.Instance.MaxFullHistoryCatchUpRounds);
        }

        [Fact]
        public void Open_chat_window_uses_former_Mobile_sizes_on_Unsupported_and_Low()
        {
            Assert.Equal(30, UnsupportedSyncWorkload.Instance.InitialUiMessageWindow);
            Assert.Equal(80, UnsupportedSyncWorkload.Instance.MaxUiMessageWindow);
            Assert.Equal(30, LowSyncWorkload.Instance.InitialUiMessageWindow);
            Assert.Equal(80, LowSyncWorkload.Instance.MaxUiMessageWindow);
        }

        [Fact]
        public void Open_chat_window_uses_former_desktop_sizes_on_High()
        {
            Assert.Equal(50, HighSyncWorkload.Instance.InitialUiMessageWindow);
            Assert.Equal(150, HighSyncWorkload.Instance.MaxUiMessageWindow);
        }
    }
}
