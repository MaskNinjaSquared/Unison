// =============================================================================
// IHistorySqliteChunkAppliedHost
//
// Side effects for HistorySqliteChunkAppliedDispatch.
// =============================================================================

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IHistorySqliteChunkAppliedHost
    {
        int SqliteHistoryFullFinalizeDelayMs { get; }

        int SqliteHistoryFinalizeQuietMs { get; }

        bool IsEnrichmentAwaitingHeavyHistory { get; }

        int SqliteHistoryConversationsAccumulated { get; }

        /// <summary>
        /// True after the first-sync list hold was released. Trailing StatusV3/PushName/Recent
        /// must not republish active initial-sync progress (safe-mode / batch-loop hang).
        /// </summary>
        bool FirstSyncListAlreadyReleased { get; }

        void MarkHistorySyncReceivedNow();

        void NoteFullHistoryCatchUpProgress(string reason);

        int AddSqliteHistoryConversations(int count);

        void PublishInitialSyncProgress(
            bool isActive,
            bool isCompleted,
            int processed,
            int total,
            string stage);

        int NextSqliteHistoryFinalizeGeneration();

        void FinalizeSqliteHistoryAfterQuiet(int generation, int delayMs, int processed);

        void CompleteUserResyncHistoryWait(string reason);

        void RememberLastHistorySyncType(string type);

        void ReleaseEnrichmentAfterHeavyHistory(string type);

        void LogHistorySqliteChunkApplied(string type, int count, int accumulated);
    }
}
