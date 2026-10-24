// =============================================================================
// HistorySqliteChunkAppliedDispatch
//
// Body of NotifyHistorySqliteChunkApplied after classify. Progress / finalize /
// enrichment I/O stay on the host.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class HistorySqliteChunkAppliedDispatch
    {
        public static void Run(
            IHistorySqliteChunkAppliedHost host,
            string syncType,
            int conversationCount)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            HistorySqliteChunkClassify.Classify(
                syncType,
                out string type,
                out bool isOnDemand,
                out bool isFull);
            int count = HistorySqliteChunkClassify.ClampConversationCount(conversationCount);

            host.MarkHistorySyncReceivedNow();
            host.NoteFullHistoryCatchUpProgress(
                "sqlite-applied:" + type + ":conversations=" + count);

            if (!isOnDemand)
            {
                int processed = host.AddSqliteHistoryConversations(count);
                int total = HistorySqliteChunkClassify.ProgressTotal(isFull, processed);

                host.PublishInitialSyncProgress(
                    true,
                    false,
                    processed,
                    total,
                    HistorySqliteChunkClassify.AppliedProgressReason(isFull));

                int generation = host.NextSqliteHistoryFinalizeGeneration();
                int delayMs = HistorySqliteChunkClassify.FinalizeDelayMs(
                    isFull,
                    host.SqliteHistoryFullFinalizeDelayMs,
                    host.SqliteHistoryFinalizeQuietMs);
                host.FinalizeSqliteHistoryAfterQuiet(generation, delayMs, processed);
            }

            if (HistorySqliteChunkClassify.ShouldCompleteUserResync(count, isFull))
            {
                host.CompleteUserResyncHistoryWait("history-sqlite:" + type);
            }

            if (HistorySqliteChunkClassify.ShouldReleaseEnrichment(
                    isFull,
                    host.IsEnrichmentAwaitingHeavyHistory,
                    count))
            {
                host.RememberLastHistorySyncType(type);
                host.ReleaseEnrichmentAfterHeavyHistory(type);
            }

            host.LogHistorySqliteChunkApplied(
                type,
                count,
                host.SqliteHistoryConversationsAccumulated);
        }
    }
}
