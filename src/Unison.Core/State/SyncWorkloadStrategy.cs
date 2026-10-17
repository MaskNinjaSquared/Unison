// =============================================================================
// SyncWorkloadStrategy
//
// How heavy the first sync and its follow-up may be on this device. One subclass
// per DeviceCategory so the numbers live with the tier, not in a switch at every
// call site. Platform (Mobile vs desktop) is separate: Mobile alone decides
// whether the chat list waits for IsCompleted before releasing rows.
// =============================================================================
using Unison.Core.Models;

namespace Unison.Core.State
{
    public abstract class SyncWorkloadStrategy
    {
        public abstract DeviceCategory Category { get; }

        /// <summary>Newest bodies kept per conversation when a history chunk is written.</summary>
        public abstract int MaxMessagesPerConversation { get; }

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

        public override int HistoryPersistSubBatchSize => 80;

        public override bool MaterializeHistoryThumbsDuringSync => false;

        public override int ChatListReleaseBatchSize => 6;

        public override int PreviewHydrateBatchSize => 8;

        public override int AvatarFetchBatchSize => 1;

        public override int AvatarFetchInterRequestDelayMs => 1200;

        public override uint HistoryStorageQuotaMb => 256;
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

        public override int HistoryPersistSubBatchSize => 150;

        public override bool MaterializeHistoryThumbsDuringSync => true;

        public override int ChatListReleaseBatchSize => 12;

        public override int PreviewHydrateBatchSize => 15;

        public override int AvatarFetchBatchSize => 2;

        public override int AvatarFetchInterRequestDelayMs => 900;

        public override uint HistoryStorageQuotaMb => 1024;
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

        public override int HistoryPersistSubBatchSize => 500;

        public override bool MaterializeHistoryThumbsDuringSync => true;

        public override int ChatListReleaseBatchSize => 20;

        public override int PreviewHydrateBatchSize => 25;

        public override int AvatarFetchBatchSize => 12;

        public override int AvatarFetchInterRequestDelayMs => 900;

        public override uint HistoryStorageQuotaMb => 10240;
    }
}
