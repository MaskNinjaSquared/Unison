// =============================================================================
// IHistoryCatchUpFinishHost
//
// Side effects for HistoryCatchUpFinishDispatch after catch-up progress ends.
// =============================================================================
using System;
using Unison.Core.State;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IHistoryCatchUpFinishHost
    {
        HistoryCatchUpProgress FinishCatchUpProgress();

        void WriteCatchUpFinishJournal(string reason, HistoryCatchUpProgress last);

        void EndHistoryCatchUpBanner(string reason);

        bool IsFirstSyncListHoldInProgress { get; }

        /// <summary>
        /// The chat list is still held for the first history sync of this session
        /// (<c>IHistoryService.IsAwaitingFirstHistorySync</c>), chunk or not.
        /// </summary>
        bool IsListAwaitingFirstHistorySync { get; }

        /// <summary>
        /// PairingBootstrap already observed or is applying a non-on-demand history
        /// chunk — catch-up finish must not unlock the list.
        /// </summary>
        bool IsPairingBootstrapChunkSeenOrApplying { get; }

        int SqliteHistoryConversationsAccumulated { get; }

        int InitialSyncProcessedConversations { get; }

        void RequestFirstSyncListReleaseAfterNames(string reason, int processed, int total);

        /// <summary>
        /// Explicit delay (not the tier default from <c>SyncWorkloadStrategy</c>).
        /// Catch-up finish uses a fixed quiet gap before deferred-avatar wake.
        /// </summary>
        void ScheduleDeferredAvatars(string reason, TimeSpan delay);

        void ClearEnrichmentAwaitingHeavyHistory();

        void TrySchedulePostMessageEnrichment(string reason);

        /// <summary>
        /// After a loud catch-up finish: kick the Mobile silent chain when tips are
        /// still stale, or disarm when already fresh.
        /// </summary>
        void NotifySilentChainAfterLoudCatchUpFinish(string finishReason);
    }
}
