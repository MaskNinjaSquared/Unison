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
    }
}
