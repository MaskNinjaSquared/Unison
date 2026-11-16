// =============================================================================
// SyncProductPolicy
//
// Unison product layer over the WhatsApp client: maps the device tier
// (SyncWorkloadStrategy) and the live frugal decision (SyncBudget) to the neutral
// knobs the client executes with. Tier / frugal never cross into the client.
// =============================================================================
using System;
using Unison.Core.Contracts.Tuning;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Core.Product
{
    public sealed class SyncProductPolicy :
        ISyncLoadMode,
        IHistorySyncTuning,
        IEnrichmentTuning,
        IStartupMaintenanceTuning,
        ISyncPhaseTuning
    {
        private const int MinHistoryFinalizeMs = 200;

        private const int DelayedBulkMinOfflineCount = 50;
        private static readonly TimeSpan FrugalPostReplayQuietWait = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan PostReplayQuietWaitDefault = TimeSpan.FromSeconds(12);

        private readonly SyncWorkloadStrategy _workload;
        private readonly Func<bool> _isFrugal;

        public SyncProductPolicy(SyncWorkloadStrategy workload, Func<bool> isFrugal)
        {
            _workload = workload ?? throw new ArgumentNullException(nameof(workload));
            _isFrugal = isFrugal ?? throw new ArgumentNullException(nameof(isFrugal));
        }

        public bool PreferFrugal => _isFrugal();

        // --- IHistorySyncTuning ----------------------------------------------

        int IHistorySyncTuning.MaxMessagesPerConversation => _workload.MaxMessagesPerConversation;

        bool IHistorySyncTuning.MaterializeThumbsDuringSync => _workload.MaterializeHistoryThumbsDuringSync;

        int IHistorySyncTuning.PersistSubBatchSize => _workload.HistoryPersistSubBatchSize;

        int IHistorySyncTuning.CatchUpStagnantCyclesBeforeStop =>
            _workload.PacingFor(_isFrugal()).CatchUpStagnantCyclesBeforeStop;

        int IHistorySyncTuning.MaxFullHistoryCatchUpRounds => Math.Max(0, _workload.MaxFullHistoryCatchUpRounds);

        bool IHistorySyncTuning.AllowStagnantRepairCascade => _workload.AllowStagnantHistoryRepairCascade;

        TimeSpan IHistorySyncTuning.SilentFullHistoryDelay => SilentFullHistoryPolicy.DelayFor(_workload.Category);

        int IHistorySyncTuning.HistoryFinalizeQuietMs =>
            Math.Max(MinHistoryFinalizeMs, _workload.HistoryFinalizeQuietMs);

        int IHistorySyncTuning.HistoryFullFinalizeDelayMs =>
            Math.Max(MinHistoryFinalizeMs, _workload.HistoryFullFinalizeDelayMs);

        // --- IEnrichmentTuning -----------------------------------------------

        int IEnrichmentTuning.AvatarFetchBatchSize => _workload.AvatarFetchBatchSize;

        int IEnrichmentTuning.AvatarFetchInterRequestDelayMs => _workload.AvatarFetchInterRequestDelayMs;

        int IEnrichmentTuning.NameResolveMaxPerPass => _workload.NameResolveMaxPerPass;

        int IEnrichmentTuning.NameResolveChunkSize => _workload.NameResolveChunkSize;

        int IEnrichmentTuning.NameResolveInterChunkDelayMs => _workload.NameResolveInterChunkDelayMs;

        int IEnrichmentTuning.GroupRosterBatchSize => _workload.GroupRosterBatchSize;

        int IEnrichmentTuning.GroupRosterInterRequestDelayMs => _workload.GroupRosterInterRequestDelayMs;

        int IEnrichmentTuning.GroupRosterNextBatchDelaySeconds => _workload.GroupRosterNextBatchDelaySeconds;

        int IEnrichmentTuning.GroupMetadataBatchSize => _workload.GroupMetadataBatchSize;

        int IEnrichmentTuning.GroupMetadataPauseMs => _workload.GroupMetadataPauseMs;

        int IEnrichmentTuning.GroupListingResumeSettleMs => _workload.GroupListingResumeSettleMs;

        // Roster batch × 8 so Mobile yields often without tiny commits.
        int IEnrichmentTuning.GroupRosterBuildChunkSize => Math.Max(1, _workload.GroupRosterBatchSize) * 8;

        // Low tiers: longer debounce so RoleChange storms coalesce into fewer UI flushes.
        TimeSpan? IEnrichmentTuning.GroupRosterFlushDebounce => IsLowTier ? TimeSpan.FromMilliseconds(750) : (TimeSpan?)null;

        TimeSpan IEnrichmentTuning.DeferredAvatarRetryDelay =>
            TimeSpan.FromSeconds(_workload.DeferredAvatarRetryDelaySeconds);

        int IEnrichmentTuning.DeferredAvatarMaxRetries => _workload.DeferredAvatarMaxRetries;

        bool IEnrichmentTuning.SerializePostSyncEnrichment => _workload.SerializePostSyncEnrichment;

        TimeSpan IEnrichmentTuning.PostSyncEnrichmentQuietWait =>
            TimeSpan.FromSeconds(_workload.PostSyncEnrichmentQuietSeconds);

        // --- ISyncPhaseTuning ------------------------------------------------

        TimeSpan ISyncPhaseTuning.StartupQuietFloor(bool firstSyncListHold) =>
            TimeSpan.FromMilliseconds(PacingFor(firstSyncListHold).StartupQuietFloorMs);

        TimeSpan ISyncPhaseTuning.DeferredStartupQuietWait(bool firstSyncListHold) =>
            TimeSpan.FromMilliseconds(PacingFor(firstSyncListHold).DeferredStartupQuietMs);

        TimeSpan ISyncPhaseTuning.CatchUpIdleAbort =>
            TimeSpan.FromSeconds(PacingFor(firstSyncListHold: false).CatchUpIdleAbortSeconds);

        private SyncPacing PacingFor(bool firstSyncListHold) =>
            SyncPhasePacing.For(_workload, _isFrugal(), firstSyncListHold);

        // --- IStartupMaintenanceTuning --------------------------------------

        bool IStartupMaintenanceTuning.DeferredStartupRequiresVisibleWindow => _isFrugal();

        bool IStartupMaintenanceTuning.DeferredStartupRequiresMemoryHeadroom => _isFrugal();

        bool IStartupMaintenanceTuning.ResolveMissingNamesAtStartup => !_isFrugal();

        TimeSpan IStartupMaintenanceTuning.PostReplayQuietWait =>
            _isFrugal() ? FrugalPostReplayQuietWait : PostReplayQuietWaitDefault;

        // Large offline drains get a delayed bulk tip scan unless already frugal.
        bool IStartupMaintenanceTuning.RunDelayedBulkAfterReplay(int offlineCount) =>
            offlineCount >= DelayedBulkMinOfflineCount && !_isFrugal();

        private bool IsLowTier =>
            _workload.Category == DeviceCategory.Low || _workload.Category == DeviceCategory.Unsupported;
    }
}
