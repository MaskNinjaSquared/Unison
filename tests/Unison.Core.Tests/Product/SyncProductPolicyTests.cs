using System;
using Unison.Core.Contracts.Tuning;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.Product;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.Product
{
    public sealed class SyncProductPolicyTests
    {
        [Fact]
        public void Frugal_follows_the_live_decision()
        {
            bool frugal = false;
            ISyncLoadMode mode = new SyncProductPolicy(SyncWorkloadStrategy.For(DeviceCategory.High), () => frugal);

            Assert.False(mode.PreferFrugal);
            frugal = true;
            Assert.True(mode.PreferFrugal);
        }

        [Fact]
        public void Stagnant_cycles_switch_with_frugal_pacing()
        {
            bool frugal = false;
            SyncWorkloadStrategy workload = SyncWorkloadStrategy.For(DeviceCategory.Low);
            IHistorySyncTuning tuning = new SyncProductPolicy(workload, () => frugal);

            Assert.Equal(workload.PacingFor(false).CatchUpStagnantCyclesBeforeStop, tuning.CatchUpStagnantCyclesBeforeStop);
            frugal = true;
            Assert.Equal(workload.PacingFor(true).CatchUpStagnantCyclesBeforeStop, tuning.CatchUpStagnantCyclesBeforeStop);
        }

        [Theory]
        [InlineData(DeviceCategory.High)]
        [InlineData(DeviceCategory.Low)]
        [InlineData(DeviceCategory.Unsupported)]
        public void History_knobs_come_from_the_tier(DeviceCategory category)
        {
            SyncWorkloadStrategy workload = SyncWorkloadStrategy.For(category);
            IHistorySyncTuning tuning = new SyncProductPolicy(workload, () => false);

            Assert.Equal(workload.MaxMessagesPerConversation, tuning.MaxMessagesPerConversation);
            Assert.Equal(workload.MaterializeHistoryThumbsDuringSync, tuning.MaterializeThumbsDuringSync);
            Assert.Equal(workload.HistoryPersistSubBatchSize, tuning.PersistSubBatchSize);
            Assert.Equal(workload.AllowStagnantHistoryRepairCascade, tuning.AllowStagnantRepairCascade);
            Assert.Equal(SilentFullHistoryPolicy.DelayFor(category), tuning.SilentFullHistoryDelay);
        }

        [Fact]
        public void Roster_build_chunk_is_eight_batches()
        {
            SyncWorkloadStrategy workload = SyncWorkloadStrategy.For(DeviceCategory.High);
            IEnrichmentTuning tuning = new SyncProductPolicy(workload, () => false);

            Assert.Equal(Math.Max(1, workload.GroupRosterBatchSize) * 8, tuning.GroupRosterBuildChunkSize);
        }

        [Fact]
        public void Frugal_startup_requires_visibility_and_headroom_and_skips_missing_names()
        {
            bool frugal = true;
            IStartupMaintenanceTuning tuning = new SyncProductPolicy(
                SyncWorkloadStrategy.For(DeviceCategory.Low),
                () => frugal);

            Assert.True(tuning.DeferredStartupRequiresVisibleWindow);
            Assert.True(tuning.DeferredStartupRequiresMemoryHeadroom);
            Assert.False(tuning.ResolveMissingNamesAtStartup);

            frugal = false;
            Assert.False(tuning.DeferredStartupRequiresVisibleWindow);
            Assert.False(tuning.DeferredStartupRequiresMemoryHeadroom);
            Assert.True(tuning.ResolveMissingNamesAtStartup);
        }

        [Fact]
        public void Post_replay_quiet_is_shorter_when_frugal()
        {
            bool frugal = true;
            IStartupMaintenanceTuning tuning = new SyncProductPolicy(
                SyncWorkloadStrategy.For(DeviceCategory.High),
                () => frugal);

            Assert.Equal(TimeSpan.FromSeconds(8), tuning.PostReplayQuietWait);
            frugal = false;
            Assert.Equal(TimeSpan.FromSeconds(12), tuning.PostReplayQuietWait);
        }

        [Fact]
        public void Delayed_bulk_only_above_threshold_without_frugal()
        {
            bool frugal = false;
            IStartupMaintenanceTuning tuning = new SyncProductPolicy(
                SyncWorkloadStrategy.For(DeviceCategory.High),
                () => frugal);

            Assert.True(tuning.RunDelayedBulkAfterReplay(50));
            Assert.False(tuning.RunDelayedBulkAfterReplay(49));
            frugal = true;
            Assert.False(tuning.RunDelayedBulkAfterReplay(50));
        }

        [Theory]
        [InlineData(DeviceCategory.High, false)]
        [InlineData(DeviceCategory.Low, true)]
        [InlineData(DeviceCategory.Unsupported, true)]
        public void Low_tiers_debounce_roster_flushes(DeviceCategory category, bool debounced)
        {
            IEnrichmentTuning tuning = new SyncProductPolicy(SyncWorkloadStrategy.For(category), () => false);

            Assert.Equal(debounced ? TimeSpan.FromMilliseconds(750) : (TimeSpan?)null, tuning.GroupRosterFlushDebounce);
        }

        [Theory]
        [InlineData(DeviceCategory.High, false)]
        [InlineData(DeviceCategory.Low, true)]
        [InlineData(DeviceCategory.Unsupported, false)]
        public void Phase_quiet_follows_tier_pacing_and_list_hold(DeviceCategory category, bool frugal)
        {
            SyncWorkloadStrategy workload = SyncWorkloadStrategy.For(category);
            ISyncPhaseTuning tuning = new SyncProductPolicy(workload, () => frugal);

            SyncPacing open = SyncPhasePacing.For(workload, frugal, firstSyncListHold: false);
            SyncPacing held = SyncPhasePacing.For(workload, frugal, firstSyncListHold: true);

            Assert.Equal(TimeSpan.FromMilliseconds(open.StartupQuietFloorMs), tuning.StartupQuietFloor(false));
            Assert.Equal(TimeSpan.FromMilliseconds(held.StartupQuietFloorMs), tuning.StartupQuietFloor(true));
            Assert.Equal(TimeSpan.FromMilliseconds(open.DeferredStartupQuietMs), tuning.DeferredStartupQuietWait(false));
            Assert.Equal(TimeSpan.FromMilliseconds(held.DeferredStartupQuietMs), tuning.DeferredStartupQuietWait(true));
            Assert.Equal(TimeSpan.FromSeconds(open.CatchUpIdleAbortSeconds), tuning.CatchUpIdleAbort);
        }

        [Fact]
        public void List_hold_shortens_startup_quiet_floor()
        {
            ISyncPhaseTuning tuning = new SyncProductPolicy(SyncWorkloadStrategy.For(DeviceCategory.Low), () => true);

            Assert.True(tuning.StartupQuietFloor(true) <= TimeSpan.FromMilliseconds(SyncPhasePacing.HoldStartupQuietFloorMs));
            Assert.True(tuning.DeferredStartupQuietWait(true) <= TimeSpan.FromMilliseconds(SyncPhasePacing.HoldDeferredStartupQuietMs));
        }

        [Theory]
        [InlineData(DeviceCategory.High)]
        [InlineData(DeviceCategory.Low)]
        [InlineData(DeviceCategory.Unsupported)]
        public void History_finalize_waits_never_drop_below_200ms(DeviceCategory category)
        {
            SyncWorkloadStrategy workload = SyncWorkloadStrategy.For(category);
            IHistorySyncTuning tuning = new SyncProductPolicy(workload, () => false);

            Assert.Equal(Math.Max(200, workload.HistoryFinalizeQuietMs), tuning.HistoryFinalizeQuietMs);
            Assert.Equal(Math.Max(200, workload.HistoryFullFinalizeDelayMs), tuning.HistoryFullFinalizeDelayMs);
        }

        [Theory]
        [InlineData(DeviceCategory.High)]
        [InlineData(DeviceCategory.Low)]
        [InlineData(DeviceCategory.Unsupported)]
        public void Post_sync_enrichment_knobs_come_from_the_tier(DeviceCategory category)
        {
            SyncWorkloadStrategy workload = SyncWorkloadStrategy.For(category);
            IEnrichmentTuning tuning = new SyncProductPolicy(workload, () => false);

            Assert.Equal(workload.SerializePostSyncEnrichment, tuning.SerializePostSyncEnrichment);
            Assert.Equal(TimeSpan.FromSeconds(workload.PostSyncEnrichmentQuietSeconds), tuning.PostSyncEnrichmentQuietWait);
        }

        [Theory]
        [InlineData(DeviceCategory.High, 812)]
        [InlineData(DeviceCategory.Low, 812)]
        [InlineData(DeviceCategory.Unsupported, 100)]
        public void Initial_pre_key_upload_follows_tier(DeviceCategory category, int expected)
        {
            ISocketBootstrapTuning tuning = new SyncProductPolicy(SyncWorkloadStrategy.For(category), () => false);

            Assert.Equal(expected, tuning.InitialPreKeyUploadCount);
        }

        [Theory]
        [InlineData(DeviceCategory.High, 4096)]
        [InlineData(DeviceCategory.Low, 1024)]
        [InlineData(DeviceCategory.Unsupported, 512)]
        public void Profile_picture_cache_cap_follows_tier(DeviceCategory category, int expected)
        {
            IProfilePictureCacheTuning tuning = new SyncProductPolicy(SyncWorkloadStrategy.For(category), () => false);

            Assert.Equal(expected, tuning.MaxEntries);
        }

        [Theory]
        [InlineData(DeviceCategory.High, 128)]
        [InlineData(DeviceCategory.Low, 32)]
        [InlineData(DeviceCategory.Unsupported, 16)]
        public void Group_roster_memory_cap_follows_tier(DeviceCategory category, int expected)
        {
            IGroupRosterStoreTuning tuning = new SyncProductPolicy(SyncWorkloadStrategy.For(category), () => false);

            Assert.Equal(expected, tuning.MemoryMaxGroups);
        }

        [Fact]
        public void Lid_mapping_persist_knobs_match_high_tier_workload()
        {
            SyncWorkloadStrategy workload = SyncWorkloadStrategy.For(DeviceCategory.High);
            ILidMappingPersistTuning tuning = new SyncProductPolicy(workload, () => false);

            Assert.Equal(workload.LidMappingPersistBatchSize, tuning.BatchSize);
            Assert.Equal(workload.LidMappingPersistDelayMs, tuning.DelayMs);
            Assert.Equal(workload.LidMappingMemoryCacheMaxEntries, tuning.MemoryCacheMaxEntries);
            Assert.Equal(workload.LidMappingWarmMaxPairs, tuning.WarmMaxPairs);
        }

        [Theory]
        [InlineData(DeviceCategory.High, 20, 25)]
        [InlineData(DeviceCategory.Low, 8, 10)]
        [InlineData(DeviceCategory.Unsupported, 6, 8)]
        public void Chat_list_ui_batches_follow_tier(DeviceCategory category, int release, int preview)
        {
            IChatListUiTuning tuning = new SyncProductPolicy(SyncWorkloadStrategy.For(category), () => false);

            Assert.Equal(release, tuning.ChatListReleaseBatchSize);
            Assert.Equal(preview, tuning.PreviewHydrateBatchSize);
        }

        [Theory]
        [InlineData(DeviceCategory.High, 50, 150)]
        [InlineData(DeviceCategory.Low, 30, 80)]
        [InlineData(DeviceCategory.Unsupported, 30, 80)]
        public void Chat_window_caps_follow_tier(DeviceCategory category, int initial, int max)
        {
            IChatWindowTuning tuning = new SyncProductPolicy(SyncWorkloadStrategy.For(category), () => false);

            Assert.Equal(initial, tuning.InitialUiMessageWindow);
            Assert.Equal(max, tuning.MaxUiMessageWindow);
        }
    }
}
