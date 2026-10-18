// =============================================================================
// SyncWorkloadStrategy
//
// How heavy the first sync and its follow-up may be on this device. One subclass
// per DeviceCategory so the numbers live with the tier, not in a switch at every
// call site. Whether the chat list waits for the first history sync is not a
// tier decision: every platform holds it (see ChatListSyncHold).
// =============================================================================
using Unison.Core.Models;

namespace Unison.Core.State
{
    public abstract class SyncWorkloadStrategy
    {
        public abstract DeviceCategory Category { get; }

        /// <summary>Newest bodies kept per conversation when a history chunk is written.</summary>
        public abstract int MaxMessagesPerConversation { get; }

        /// <summary>
        /// Bubble VMs materialized when a chat opens. Former Mobile (30) vs desktop (50) split,
        /// keyed by memory tier instead of DeviceFamily.
        /// </summary>
        public abstract int InitialUiMessageWindow { get; }

        /// <summary>Hard cap on timeline VMs while the chat is open (trim after live sync / load-more).</summary>
        public abstract int MaxUiMessageWindow { get; }

        /// <summary>How many history rows one SQLite transaction may write.</summary>
        public abstract int HistoryPersistSubBatchSize { get; }

        /// <summary>Whether first-sync materializes JPEG thumbs before the SQLite write.</summary>
        public abstract bool MaterializeHistoryThumbsDuringSync { get; }

        /// <summary>How many chat rows the list may release per UI turn during batch render.</summary>
        public abstract int ChatListReleaseBatchSize { get; }

        /// <summary>How many SQLite preview rows merge into the store per UI tick.</summary>
        public abstract int PreviewHydrateBatchSize { get; }

        /// <summary>Network avatar fetches in one background pass.</summary>
        public abstract int AvatarFetchBatchSize { get; }

        /// <summary>Pause between avatar fetches in one pass.</summary>
        public abstract int AvatarFetchInterRequestDelayMs { get; }

        /// <summary>StorageQuotaMb declared to the phone in HistorySyncConfig.</summary>
        public abstract uint HistoryStorageQuotaMb { get; }

        /// <summary>
        /// After the first sync, run names → contacts → one avatar pass → groups one after
        /// another with quiet gaps, instead of firing the background resolution cascade.
        /// </summary>
        public abstract bool SerializePostSyncEnrichment { get; }

        /// <summary>Quiet gap between serial enrichment steps.</summary>
        public abstract int PostSyncEnrichmentQuietSeconds { get; }

        /// <summary>
        /// Adjacent FULL_HISTORY_SYNC_ON_DEMAND pages after the first quiet lot. Zero means the
        /// first lot is enough — no soft-reconnect cascade that would refill SQLite on a tiny
        /// device after MaxMessagesPerConversation already capped each chunk.
        /// </summary>
        public abstract int MaxFullHistoryCatchUpRounds { get; }

        /// <summary>
        /// Quiet ms after a non-Full history chunk before the list treats sync as complete.
        /// Longer on slow devices so a late next chunk is not mistaken for the end.
        /// </summary>
        public abstract int HistoryFinalizeQuietMs { get; }

        /// <summary>Quiet ms after a Full history chunk before finalize (shorter than non-Full).</summary>
        public abstract int HistoryFullFinalizeDelayMs { get; }

        /// <summary>Per-group metadata queries in one fallback pass (groups the listing missed).</summary>
        public abstract int GroupMetadataBatchSize { get; }

        /// <summary>Pause between those metadata queries.</summary>
        public abstract int GroupMetadataPauseMs { get; }

        /// <summary>Member pictures fetched per roster batch when a group is open.</summary>
        public abstract int GroupRosterBatchSize { get; }

        /// <summary>Pause between member picture fetches in one batch.</summary>
        public abstract int GroupRosterInterRequestDelayMs { get; }

        /// <summary>Wait before the next roster batch of the same group.</summary>
        public abstract int GroupRosterNextBatchDelaySeconds { get; }

        /// <summary>Pacing while <see cref="SyncBudget.IsFrugal"/> is true.</summary>
        protected abstract SyncPacing FrugalPacing { get; }

        /// <summary>Pacing while the budget has headroom.</summary>
        protected abstract SyncPacing GenerousPacing { get; }

        public SyncPacing PacingFor(bool frugal) => frugal ? FrugalPacing : GenerousPacing;

        public static SyncWorkloadStrategy For(DeviceCategory category)
        {
            switch (category)
            {
                case DeviceCategory.Unsupported:
                    return UnsupportedSyncWorkload.Instance;
                case DeviceCategory.Low:
                    return LowSyncWorkload.Instance;
                default:
                    return HighSyncWorkload.Instance;
            }
        }
    }

    /// <summary>512 MB class (~185 MB app limit): survive the sync, defer decoration.</summary>
    public sealed class UnsupportedSyncWorkload : SyncWorkloadStrategy
    {
        public static readonly UnsupportedSyncWorkload Instance = new UnsupportedSyncWorkload();

        private UnsupportedSyncWorkload()
        {
        }

        public override DeviceCategory Category => DeviceCategory.Unsupported;

        public override int MaxMessagesPerConversation => 50;

        public override int InitialUiMessageWindow => 30;

        public override int MaxUiMessageWindow => 80;

        public override int HistoryPersistSubBatchSize => 80;

        public override bool MaterializeHistoryThumbsDuringSync => false;

        public override int ChatListReleaseBatchSize => 6;

        public override int PreviewHydrateBatchSize => 8;

        public override int AvatarFetchBatchSize => 1;

        public override int AvatarFetchInterRequestDelayMs => 1200;

        public override uint HistoryStorageQuotaMb => 256;

        public override bool SerializePostSyncEnrichment => true;

        public override int PostSyncEnrichmentQuietSeconds => 12;

        public override int MaxFullHistoryCatchUpRounds => 0;

        public override int HistoryFinalizeQuietMs => 6000;

        public override int HistoryFullFinalizeDelayMs => 2000;

        public override int GroupMetadataBatchSize => 5;

        public override int GroupMetadataPauseMs => 400;

        public override int GroupRosterBatchSize => 4;

        public override int GroupRosterInterRequestDelayMs => 1200;

        public override int GroupRosterNextBatchDelaySeconds => 40;

        protected override SyncPacing FrugalPacing { get; } = new SyncPacing(4000, 8000, 45, 3);

        protected override SyncPacing GenerousPacing { get; } = new SyncPacing(1000, 1800, 35, 2);
    }

    /// <summary>1 GB class (~390 MB app limit): economical, not extreme.</summary>
    public sealed class LowSyncWorkload : SyncWorkloadStrategy
    {
        public static readonly LowSyncWorkload Instance = new LowSyncWorkload();

        private LowSyncWorkload()
        {
        }

        public override DeviceCategory Category => DeviceCategory.Low;

        public override int MaxMessagesPerConversation => 120;

        // Same open-chat window the Mobile SKU used — Low is the 1 GB / capped-Mobile tier.
        public override int InitialUiMessageWindow => 30;

        public override int MaxUiMessageWindow => 80;

        public override int HistoryPersistSubBatchSize => 150;

        public override bool MaterializeHistoryThumbsDuringSync => true;

        public override int ChatListReleaseBatchSize => 12;

        public override int PreviewHydrateBatchSize => 15;

        public override int AvatarFetchBatchSize => 2;

        public override int AvatarFetchInterRequestDelayMs => 900;

        public override uint HistoryStorageQuotaMb => 1024;

        public override bool SerializePostSyncEnrichment => true;

        public override int PostSyncEnrichmentQuietSeconds => 8;

        // First quiet lot is enough on 1 GB / Mobile — adjacent FULL_HISTORY pages after
        // InitialBootstrap were killing Creators (enrich + next lot in parallel). High keeps
        // pagination; Unsupported already used zero.
        public override int MaxFullHistoryCatchUpRounds => 0;

        public override int HistoryFinalizeQuietMs => 4500;

        public override int HistoryFullFinalizeDelayMs => 1500;

        public override int GroupMetadataBatchSize => 12;

        public override int GroupMetadataPauseMs => 200;

        public override int GroupRosterBatchSize => 8;

        public override int GroupRosterInterRequestDelayMs => 900;

        public override int GroupRosterNextBatchDelaySeconds => 30;

        protected override SyncPacing FrugalPacing { get; } = new SyncPacing(3000, 6000, 45, 3);

        protected override SyncPacing GenerousPacing { get; } = new SyncPacing(1000, 1800, 35, 2);
    }

    /// <summary>2 GB+ / desktop: current generous defaults.</summary>
    public sealed class HighSyncWorkload : SyncWorkloadStrategy
    {
        public static readonly HighSyncWorkload Instance = new HighSyncWorkload();

        private HighSyncWorkload()
        {
        }

        public override DeviceCategory Category => DeviceCategory.High;

        public override int MaxMessagesPerConversation => 250;

        // Former desktop window — High has the RAM headroom for it.
        public override int InitialUiMessageWindow => 50;

        public override int MaxUiMessageWindow => 150;

        public override int HistoryPersistSubBatchSize => 500;

        public override bool MaterializeHistoryThumbsDuringSync => true;

        public override int ChatListReleaseBatchSize => 20;

        public override int PreviewHydrateBatchSize => 25;

        public override int AvatarFetchBatchSize => 12;

        public override int AvatarFetchInterRequestDelayMs => 900;

        public override uint HistoryStorageQuotaMb => 10240;

        public override bool SerializePostSyncEnrichment => false;

        public override int PostSyncEnrichmentQuietSeconds => 5;

        public override int MaxFullHistoryCatchUpRounds => 60;

        public override int HistoryFinalizeQuietMs => 2800;

        public override int HistoryFullFinalizeDelayMs => 900;

        public override int GroupMetadataBatchSize => 25;

        public override int GroupMetadataPauseMs => 120;

        public override int GroupRosterBatchSize => 16;

        public override int GroupRosterInterRequestDelayMs => 900;

        public override int GroupRosterNextBatchDelaySeconds => 20;

        protected override SyncPacing FrugalPacing { get; } = new SyncPacing(3000, 6000, 45, 3);

        protected override SyncPacing GenerousPacing { get; } = new SyncPacing(1000, 1800, 35, 2);
    }
}
