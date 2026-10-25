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

        // --- History persist / UI windows ------------------------------------

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

        // --- Avatars / post-sync enrichment ---------------------------------

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

        // --- Catch-up / FULL_HISTORY ----------------------------------------

        /// <summary>
        /// Adjacent FULL_HISTORY_SYNC_ON_DEMAND pages after the first quiet lot. Zero means the
        /// first lot is enough — no soft-reconnect cascade that would refill SQLite on a tiny
        /// device after MaxMessagesPerConversation already capped each chunk.
        /// </summary>
        public abstract int MaxFullHistoryCatchUpRounds { get; }

        /// <summary>
        /// After stagnant / max-continues catch-up while tips still look stale: latch force
        /// repair, run hydration→FULL_HISTORY, and let periodic safety re-solicit. Low /
        /// Unsupported keep this false — otherwise Mobile burns minutes in idle-continue
        /// loops when Meta acks but never advances the tip.
        /// </summary>
        public abstract bool AllowStagnantHistoryRepairCascade { get; }

        /// <summary>
        /// Quiet ms after a non-Full history chunk before the list treats sync as complete.
        /// Longer on slow devices so a late next chunk is not mistaken for the end.
        /// </summary>
        public abstract int HistoryFinalizeQuietMs { get; }

        /// <summary>Quiet ms after a Full history chunk before finalize (shorter than non-Full).</summary>
        public abstract int HistoryFullFinalizeDelayMs { get; }

        // --- Groups / names / LID -------------------------------------------

        /// <summary>Per-group metadata queries in one fallback pass (groups the listing missed).</summary>
        public abstract int GroupMetadataBatchSize { get; }

        /// <summary>Pause between those metadata queries.</summary>
        public abstract int GroupMetadataPauseMs { get; }

        /// <summary>
        /// Quiet ms after the window becomes visible again before arming a deferred
        /// participating-groups listing (resume must not slam metadata on the UI thread).
        /// </summary>
        public abstract int GroupListingResumeSettleMs { get; }

        /// <summary>Member pictures fetched per roster batch when a group is open.</summary>
        public abstract int GroupRosterBatchSize { get; }

        /// <summary>Pause between member picture fetches in one batch.</summary>
        public abstract int GroupRosterInterRequestDelayMs { get; }

        /// <summary>Wait before the next roster batch of the same group.</summary>
        public abstract int GroupRosterNextBatchDelaySeconds { get; }

        /// <summary>Max unnamed direct JIDs one opportunistic name pass may ask about.</summary>
        public abstract int NameResolveMaxPerPass { get; }

        /// <summary>JIDs per usync IQ during name resolution.</summary>
        public abstract int NameResolveChunkSize { get; }

        /// <summary>Pause between usync chunks so keep-alives can land on Mobile.</summary>
        public abstract int NameResolveInterChunkDelayMs { get; }

        /// <summary>
        /// How many new LID↔PN pairs one disk write may take after history has released
        /// the gate. Smaller on constrained tiers so SQLite stays cheap.
        /// </summary>
        public abstract int LidMappingPersistBatchSize { get; }

        /// <summary>
        /// Quiet ms before a sub-batch LID disk flush (write-behind). Ignored while the
        /// first history sync still holds disk persist.
        /// </summary>
        public abstract int LidMappingPersistDelayMs { get; }

        /// <summary>
        /// Max PN↔LID <em>pairs</em> kept in the in-memory LRU. Each pair occupies two
        /// cache keys (forward + reverse). Mobile stays on-demand with a hard cap;
        /// High may hold more for sync peeks.
        /// </summary>
        public abstract int LidMappingMemoryCacheMaxPairs { get; }

        /// <summary>
        /// Forward pairs to seed from SQLite at session start. Always <c>0</c>:
        /// no warm on boot (any tier). RAM fills on demand; High only gets a
        /// larger <see cref="LidMappingMemoryCacheMaxPairs"/> LRU.
        /// </summary>
        public abstract int LidMappingWarmMaxPairs { get; }

        /// <summary>
        /// Cache key budget for <see cref="LidMappingMemoryCacheMaxPairs"/> (pair × 2).
        /// </summary>
        public int LidMappingMemoryCacheMaxEntries =>
            LidMappingMemoryCacheMaxPairs > 0
                ? LidMappingMemoryCacheMaxPairs * 2
                : 0;

        // --- Deferred avatar wake + pacing ----------------------------------

        /// <summary>
        /// Seconds before a deferred avatar retry fires. Longer on constrained tiers so
        /// catch-up / tip paint can finish without a 20s wake loop.
        /// </summary>
        public abstract int DeferredAvatarRetryDelaySeconds { get; }

        /// <summary>
        /// Max consecutive deferred-avatar schedules before giving up. Zero disables the
        /// timer entirely (viewport / on-demand only).
        /// </summary>
        public abstract int DeferredAvatarMaxRetries { get; }

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

        public override int AvatarFetchInterRequestDelayMs => 1500;

        public override uint HistoryStorageQuotaMb => 256;

        public override bool SerializePostSyncEnrichment => true;

        public override int PostSyncEnrichmentQuietSeconds => 15;

        public override int MaxFullHistoryCatchUpRounds => 0;

        public override bool AllowStagnantHistoryRepairCascade => false;

        public override int HistoryFinalizeQuietMs => 6000;

        public override int HistoryFullFinalizeDelayMs => 2000;

        public override int GroupMetadataBatchSize => 5;

        public override int GroupMetadataPauseMs => 400;

        public override int GroupListingResumeSettleMs => 3000;

        public override int GroupRosterBatchSize => 4;

        public override int GroupRosterInterRequestDelayMs => 1200;

        public override int GroupRosterNextBatchDelaySeconds => 40;

        public override int NameResolveMaxPerPass => 4;

        public override int NameResolveChunkSize => 2;

        public override int NameResolveInterChunkDelayMs => 1000;

        // Smallest disk lots: 512 MB class cannot afford large SQLite LID batches.
        public override int LidMappingPersistBatchSize => 8;

        public override int LidMappingPersistDelayMs => 3000;

        // On-demand only; tight LRU so warm dumps cannot pin RAM.
        public override int LidMappingMemoryCacheMaxPairs => 512;

        public override int LidMappingWarmMaxPairs => 0;

        // No background avatar wake loop — list viewport / open-chat fetch only.
        public override int DeferredAvatarRetryDelaySeconds => 60;

        public override int DeferredAvatarMaxRetries => 0;

        protected override SyncPacing FrugalPacing { get; } = new SyncPacing(5000, 10000, 50, 3);

        protected override SyncPacing GenerousPacing { get; } = new SyncPacing(1500, 2500, 40, 2);
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

        // Slightly above the old 150: Mobile (capped to Low) spends less wall time in
        // SQLite round-trips without approaching High's 500-row transactions.
        public override int HistoryPersistSubBatchSize => 220;

        // Thumbs during FULL_HISTORY compete with tip paint + SQLite on 1 GB Mobile.
        // Decode on open / media pass instead.
        public override bool MaterializeHistoryThumbsDuringSync => false;

        public override int ChatListReleaseBatchSize => 8;

        public override int PreviewHydrateBatchSize => 10;

        public override int AvatarFetchBatchSize => 1;

        public override int AvatarFetchInterRequestDelayMs => 1200;

        public override uint HistoryStorageQuotaMb => 1024;

        public override bool SerializePostSyncEnrichment => true;

        public override int PostSyncEnrichmentQuietSeconds => 12;

        // A few adjacent FULL_HISTORY pages — enough for Meta's near days, not the old
        // 12-round idle-continue burn while signals stay 0. Catch-up still defers usync
        // via HistoryCatchUpLocalWorkPolicy while the banner is up.
        public override int MaxFullHistoryCatchUpRounds => 4;

        public override bool AllowStagnantHistoryRepairCascade => false;

        // Slightly longer than High so a late chunk on slow Mobile is not mistaken for end.
        public override int HistoryFinalizeQuietMs => 2500;

        public override int HistoryFullFinalizeDelayMs => 1200;

        public override int GroupMetadataBatchSize => 12;

        public override int GroupMetadataPauseMs => 200;

        public override int GroupListingResumeSettleMs => 2500;

        public override int GroupRosterBatchSize => 8;

        public override int GroupRosterInterRequestDelayMs => 900;

        public override int GroupRosterNextBatchDelaySeconds => 35;

        public override int NameResolveMaxPerPass => 4;

        public override int NameResolveChunkSize => 2;

        public override int NameResolveInterChunkDelayMs => 700;

        public override int LidMappingPersistBatchSize => 40;

        public override int LidMappingPersistDelayMs => 1500;

        // Mobile 1 GB: on-demand only; larger LRU than Unsupported.
        public override int LidMappingMemoryCacheMaxPairs => 1024;

        public override int LidMappingWarmMaxPairs => 0;

        public override int DeferredAvatarRetryDelaySeconds => 40;

        public override int DeferredAvatarMaxRetries => 2;

        protected override SyncPacing FrugalPacing { get; } = new SyncPacing(4000, 7000, 50, 3);

        protected override SyncPacing GenerousPacing { get; } = new SyncPacing(1200, 2000, 40, 2);
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

        public override bool AllowStagnantHistoryRepairCascade => true;

        public override int HistoryFinalizeQuietMs => 1000;

        public override int HistoryFullFinalizeDelayMs => 700;

        public override int GroupMetadataBatchSize => 25;

        public override int GroupMetadataPauseMs => 120;

        public override int GroupListingResumeSettleMs => 1200;

        public override int GroupRosterBatchSize => 16;

        public override int GroupRosterInterRequestDelayMs => 900;

        public override int GroupRosterNextBatchDelaySeconds => 20;

        public override int NameResolveMaxPerPass => 10;

        public override int NameResolveChunkSize => 5;

        public override int NameResolveInterChunkDelayMs => 120;

        public override int LidMappingPersistBatchSize => 120;

        public override int LidMappingPersistDelayMs => 400;

        // Desktop: larger LRU only — still on-demand (no boot seed).
        public override int LidMappingMemoryCacheMaxPairs => 2048;

        public override int LidMappingWarmMaxPairs => 0;

        public override int DeferredAvatarRetryDelaySeconds => 20;

        public override int DeferredAvatarMaxRetries => 6;

        protected override SyncPacing FrugalPacing { get; } = new SyncPacing(3000, 6000, 45, 3);

        protected override SyncPacing GenerousPacing { get; } = new SyncPacing(1000, 1800, 35, 2);
    }
}
