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
    public sealed class SyncProductPolicy : ISyncLoadMode, IHistorySyncTuning, IEnrichmentTuning
    {
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

        private bool IsLowTier =>
            _workload.Category == DeviceCategory.Low || _workload.Category == DeviceCategory.Unsupported;
    }
}
