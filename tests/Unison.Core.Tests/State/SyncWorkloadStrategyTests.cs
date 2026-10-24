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
            Assert.False(s.AllowStagnantHistoryRepairCascade);
            Assert.Equal(6000, s.HistoryFinalizeQuietMs);
            Assert.Equal(2000, s.HistoryFullFinalizeDelayMs);
            Assert.Equal(8, s.LidMappingPersistBatchSize);
            Assert.Equal(3000, s.LidMappingPersistDelayMs);
            Assert.Equal(512, s.LidMappingMemoryCacheMaxPairs);
            Assert.Equal(0, s.LidMappingWarmMaxPairs);
            Assert.Equal(1024, s.LidMappingMemoryCacheMaxEntries);
            Assert.Equal(0, s.DeferredAvatarMaxRetries);
            Assert.Equal(60, s.DeferredAvatarRetryDelaySeconds);
            Assert.Equal(15, s.PostSyncEnrichmentQuietSeconds);
        }

        [Fact]
        public void Lid_mapping_disk_batches_shrink_with_the_device()
        {
            SyncWorkloadStrategy unsupported = UnsupportedSyncWorkload.Instance;
            SyncWorkloadStrategy low = LowSyncWorkload.Instance;
            SyncWorkloadStrategy high = HighSyncWorkload.Instance;

            Assert.True(unsupported.LidMappingPersistBatchSize < low.LidMappingPersistBatchSize);
            Assert.True(low.LidMappingPersistBatchSize < high.LidMappingPersistBatchSize);
            Assert.True(unsupported.LidMappingPersistDelayMs > low.LidMappingPersistDelayMs);
            Assert.True(low.LidMappingPersistDelayMs > high.LidMappingPersistDelayMs);
            Assert.Equal(40, low.LidMappingPersistBatchSize);
            Assert.Equal(1500, low.LidMappingPersistDelayMs);
            Assert.Equal(120, high.LidMappingPersistBatchSize);
            Assert.Equal(400, high.LidMappingPersistDelayMs);
        }

        [Fact]
        public void Lid_mapping_memory_is_on_demand_on_every_tier()
        {
            SyncWorkloadStrategy unsupported = UnsupportedSyncWorkload.Instance;
            SyncWorkloadStrategy low = LowSyncWorkload.Instance;
            SyncWorkloadStrategy high = HighSyncWorkload.Instance;

            Assert.Equal(0, unsupported.LidMappingWarmMaxPairs);
            Assert.Equal(0, low.LidMappingWarmMaxPairs);
            Assert.Equal(0, high.LidMappingWarmMaxPairs);
            Assert.True(unsupported.LidMappingMemoryCacheMaxPairs < low.LidMappingMemoryCacheMaxPairs);
            Assert.True(low.LidMappingMemoryCacheMaxPairs < high.LidMappingMemoryCacheMaxPairs);
            Assert.Equal(512, unsupported.LidMappingMemoryCacheMaxPairs);
            Assert.Equal(1024, low.LidMappingMemoryCacheMaxPairs);
            Assert.Equal(2048, high.LidMappingMemoryCacheMaxPairs);
        }

        [Fact]
        public void Low_is_between_unsupported_and_high()
        {
            SyncWorkloadStrategy low = LowSyncWorkload.Instance;
            SyncWorkloadStrategy unsupported = UnsupportedSyncWorkload.Instance;
            SyncWorkloadStrategy high = HighSyncWorkload.Instance;

            Assert.True(low.MaxMessagesPerConversation > unsupported.MaxMessagesPerConversation);
            Assert.True(low.MaxMessagesPerConversation < high.MaxMessagesPerConversation);
            Assert.False(low.MaterializeHistoryThumbsDuringSync);
            Assert.Equal(1, low.AvatarFetchBatchSize);
            Assert.Equal(4, low.MaxFullHistoryCatchUpRounds);
            Assert.Equal(0, unsupported.MaxFullHistoryCatchUpRounds);
            Assert.True(high.MaxFullHistoryCatchUpRounds > low.MaxFullHistoryCatchUpRounds);
            Assert.False(low.AllowStagnantHistoryRepairCascade);
            Assert.False(unsupported.AllowStagnantHistoryRepairCascade);
            Assert.True(high.AllowStagnantHistoryRepairCascade);
            Assert.Equal(2500, low.HistoryFinalizeQuietMs);
            Assert.Equal(1200, low.HistoryFullFinalizeDelayMs);
            Assert.Equal(220, low.HistoryPersistSubBatchSize);
            Assert.True(low.HistoryFinalizeQuietMs < unsupported.HistoryFinalizeQuietMs);
            Assert.True(low.HistoryFinalizeQuietMs > high.HistoryFinalizeQuietMs);
            Assert.True(low.HistoryPersistSubBatchSize > unsupported.HistoryPersistSubBatchSize);
            Assert.True(low.HistoryPersistSubBatchSize < high.HistoryPersistSubBatchSize);
            Assert.Equal(2, low.DeferredAvatarMaxRetries);
            Assert.Equal(40, low.DeferredAvatarRetryDelaySeconds);
            Assert.Equal(12, low.PostSyncEnrichmentQuietSeconds);
        }

        [Fact]
        public void High_keeps_the_previous_generous_defaults()
        {
            SyncWorkloadStrategy s = HighSyncWorkload.Instance;

            Assert.Equal(250, s.MaxMessagesPerConversation);
            Assert.Equal(10240u, s.HistoryStorageQuotaMb);
            Assert.False(s.SerializePostSyncEnrichment);
            Assert.Equal(60, s.MaxFullHistoryCatchUpRounds);
            Assert.True(s.AllowStagnantHistoryRepairCascade);
            Assert.Equal(1000, s.HistoryFinalizeQuietMs);
            Assert.Equal(700, s.HistoryFullFinalizeDelayMs);
            Assert.Equal(2048, s.LidMappingMemoryCacheMaxPairs);
            Assert.Equal(0, s.LidMappingWarmMaxPairs);
            Assert.Equal(6, s.DeferredAvatarMaxRetries);
            Assert.Equal(20, s.DeferredAvatarRetryDelaySeconds);
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

        [Fact]
        public void High_keeps_the_previous_frugal_and_generous_pacing()
        {
            SyncPacing frugal = HighSyncWorkload.Instance.PacingFor(frugal: true);
            Assert.Equal(3000, frugal.StartupQuietFloorMs);
            Assert.Equal(6000, frugal.DeferredStartupQuietMs);
            Assert.Equal(45, frugal.CatchUpIdleAbortSeconds);
            Assert.Equal(3, frugal.CatchUpStagnantCyclesBeforeStop);

            SyncPacing generous = HighSyncWorkload.Instance.PacingFor(frugal: false);
            Assert.Equal(1000, generous.StartupQuietFloorMs);
            Assert.Equal(1800, generous.DeferredStartupQuietMs);
            Assert.Equal(35, generous.CatchUpIdleAbortSeconds);
            Assert.Equal(2, generous.CatchUpStagnantCyclesBeforeStop);
        }

        [Fact]
        public void Low_waits_longer_than_High_when_frugal()
        {
            SyncPacing low = LowSyncWorkload.Instance.PacingFor(frugal: true);
            SyncPacing high = HighSyncWorkload.Instance.PacingFor(frugal: true);

            Assert.Equal(4000, low.StartupQuietFloorMs);
            Assert.Equal(7000, low.DeferredStartupQuietMs);
            Assert.Equal(50, low.CatchUpIdleAbortSeconds);
            Assert.Equal(3, low.CatchUpStagnantCyclesBeforeStop);
            Assert.True(low.StartupQuietFloorMs > high.StartupQuietFloorMs);
            Assert.True(low.DeferredStartupQuietMs > high.DeferredStartupQuietMs);
        }

        [Fact]
        public void Unsupported_waits_longer_when_frugal()
        {
            SyncPacing unsupported = UnsupportedSyncWorkload.Instance.PacingFor(frugal: true);
            SyncPacing low = LowSyncWorkload.Instance.PacingFor(frugal: true);

            Assert.Equal(5000, unsupported.StartupQuietFloorMs);
            Assert.Equal(10000, unsupported.DeferredStartupQuietMs);
            Assert.True(unsupported.StartupQuietFloorMs > low.StartupQuietFloorMs);
            Assert.True(unsupported.DeferredStartupQuietMs > low.DeferredStartupQuietMs);
        }

        [Fact]
        public void Unsupported_skips_adjacent_full_history_pages_Low_allows_a_budget()
        {
            Assert.Equal(0, UnsupportedSyncWorkload.Instance.MaxFullHistoryCatchUpRounds);
            Assert.Equal(4, LowSyncWorkload.Instance.MaxFullHistoryCatchUpRounds);
            Assert.True(LowSyncWorkload.Instance.MaxFullHistoryCatchUpRounds
                < HighSyncWorkload.Instance.MaxFullHistoryCatchUpRounds);
        }

        [Fact]
        public void Deferred_avatar_retry_tightens_on_constrained_tiers()
        {
            Assert.Equal(0, UnsupportedSyncWorkload.Instance.DeferredAvatarMaxRetries);
            Assert.Equal(2, LowSyncWorkload.Instance.DeferredAvatarMaxRetries);
            Assert.Equal(6, HighSyncWorkload.Instance.DeferredAvatarMaxRetries);
            Assert.True(UnsupportedSyncWorkload.Instance.DeferredAvatarRetryDelaySeconds
                > LowSyncWorkload.Instance.DeferredAvatarRetryDelaySeconds);
            Assert.True(LowSyncWorkload.Instance.DeferredAvatarRetryDelaySeconds
                > HighSyncWorkload.Instance.DeferredAvatarRetryDelaySeconds);
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

        [Fact]
        public void Name_resolve_batches_shrink_on_constrained_devices()
        {
            Assert.True(UnsupportedSyncWorkload.Instance.NameResolveMaxPerPass
                <= LowSyncWorkload.Instance.NameResolveMaxPerPass);
            Assert.True(LowSyncWorkload.Instance.NameResolveMaxPerPass
                < HighSyncWorkload.Instance.NameResolveMaxPerPass);
            Assert.True(LowSyncWorkload.Instance.NameResolveChunkSize
                < HighSyncWorkload.Instance.NameResolveChunkSize);
            Assert.True(LowSyncWorkload.Instance.NameResolveInterChunkDelayMs
                > HighSyncWorkload.Instance.NameResolveInterChunkDelayMs);
            Assert.Equal(4, LowSyncWorkload.Instance.NameResolveMaxPerPass);
            Assert.Equal(2, LowSyncWorkload.Instance.NameResolveChunkSize);
        }
    }
}
