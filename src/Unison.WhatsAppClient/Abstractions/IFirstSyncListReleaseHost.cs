// =============================================================================
// IFirstSyncListReleaseHost
//
// Side effects for FirstSyncListReleaseDispatch.
// =============================================================================
using System;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IFirstSyncListReleaseHost
    {
        bool IsFirstSyncListHoldInProgress { get; }

        bool IsFirstSyncUnlockInFlight { get; }

        /// <summary>CAS claim: returns true when this caller owns the unlock pass.</summary>
        bool TryClaimFirstSyncUnlockInFlight();

        void MarkFirstSyncListReleaseDeferred();

        void PublishInitialSyncProgress(
            bool isActive,
            bool isCompleted,
            int processed,
            int total,
            string stage);

        void WriteFirstSyncNamesJournal(string stage, int processed);

        void LogFirstSyncListRelease(string message);

        Task ResolveNamesBeforeListReleaseAsync();

        void RecordFirstSyncNamesException(Exception error, string stage);

        void ClearSqliteHistoryConversationsAccumulated();

        /// <summary>Deferred avatar wake after list unlock (host owns the quiet delay).</summary>
        void ScheduleDeferredAvatarsAfterUnlock(string stage);

        void ClearEnrichmentAwaitingHeavyHistory();

        void TrySchedulePostMessageEnrichment(string reason);

        void ClearFirstSyncUnlockInFlight();

        void FireAndForgetFirstSyncUnlock(Func<Task> work);
    }
}
