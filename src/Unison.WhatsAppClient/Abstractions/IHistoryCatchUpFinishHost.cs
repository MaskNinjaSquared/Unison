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

        int SqliteHistoryConversationsAccumulated { get; }

        int InitialSyncProcessedConversations { get; }

        void RequestFirstSyncListReleaseAfterNames(string reason, int processed, int total);

        void ScheduleDeferredAvatars(string reason, TimeSpan delay);

        void ClearEnrichmentAwaitingHeavyHistory();

        void TrySchedulePostMessageEnrichment(string reason);
    }
}
