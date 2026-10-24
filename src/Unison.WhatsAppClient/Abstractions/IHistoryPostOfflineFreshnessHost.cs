// =============================================================================
// IHistoryPostOfflineFreshnessHost
//
// Side effects for HistoryPostOfflineFreshnessDispatch after offline drain.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.State;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IHistoryPostOfflineFreshnessHost
    {
        DateTime LastHistorySyncReceivedUtc { get; }

        string LastHistorySyncTypeOrUnknown { get; }

        bool IsHistoryCatchUpActive { get; }

        int CatchUpStagnantCyclesBeforeStop { get; }

        void NoteCatchUpProgress(string reason);

        bool TryGetFreshnessStaleReason(DateTime nowUtc, out string staleReason);

        DateTime GetNewestStoredMessageUtc();

        DateTime GetNewestNonSelfStoredMessageUtc();

        DateTime GetNewestGroupStoredMessageUtc();

        void MarkEnrichmentAwaitingHeavyHistory();

        HistoryCatchUpDrainAction DecideAfterFreshDrain(
            int offlineCount,
            int stagnantStop,
            out int stagnantCycles);

        Task RequestReconnectHistoryCatchUpAsync(string reason);

        void FinishHistoryCatchUpContinue(string reason, bool releaseEnrichment);

        void LogSyncObservedThisSession(DateTime syncUtc, string syncType);

        void LogFreshnessPassed(
            string anyText,
            string nonSelfText,
            string groupText,
            int offlineCount,
            bool catchUpActive);

        void LogCatchUpEmptyWhileTipFresh(int stagnantCycles, int stagnantStop);

        void LogFreshnessNotStaleSkip(
            string staleReason,
            string newestText,
            string tipAgeMinutes,
            int offlineCount,
            bool catchUpActive);

        void LogFreshnessStillStaleRequest(
            string staleReason,
            string newestText,
            int offlineCount);
    }
}
