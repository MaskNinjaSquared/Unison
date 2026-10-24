// =============================================================================
// HistorySqliteChunkStartedDispatch
//
// Body of NotifyHistorySqliteChunkStarted after classify.
// =============================================================================
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class HistorySqliteChunkStartedDispatch
    {
        public static void Run(
            IHistorySqliteChunkStartedHost host,
            string syncType,
            int conversationCount)
        {
            if (host == null)
            {
                throw new System.ArgumentNullException(nameof(host));
            }

            HistorySqliteChunkClassify.Classify(
                syncType,
                out string type,
                out bool isOnDemand,
                out _);
            int count = HistorySqliteChunkClassify.ClampConversationCount(conversationCount);
            host.NoteFullHistoryCatchUpProgress(
                "sqlite-started:" + type + ":conversations=" + count);
            if (isOnDemand)
            {
                return;
            }

            int processed = System.Math.Max(host.SqliteHistoryConversationsAccumulated, 0);
            // Cancel any quiet-finalize armed by a prior empty Status chunk so Bootstrap's
            // apply owns the next sqlite-finalized (otherwise Continue fires on processed=0).
            host.BumpSqliteHistoryFinalizeGeneration();
            host.PublishInitialSyncProgress(
                true,
                false,
                processed,
                0,
                HistorySqliteChunkClassify.StartingProgressReason(count));
        }
    }
}
