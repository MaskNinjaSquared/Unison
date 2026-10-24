// =============================================================================
// IHistorySqliteChunkStartedHost
//
// Side effects for HistorySqliteChunkStartedDispatch.
// =============================================================================
namespace Unison.WhatsAppClient.Abstractions
{
    public interface IHistorySqliteChunkStartedHost
    {
        int SqliteHistoryConversationsAccumulated { get; }

        void NoteFullHistoryCatchUpProgress(string reason);

        void BumpSqliteHistoryFinalizeGeneration();

        void PublishInitialSyncProgress(
            bool isActive,
            bool isCompleted,
            int processed,
            int total,
            string stage);
    }
}
