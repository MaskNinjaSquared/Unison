// =============================================================================
// HistorySqliteChunkClassify
//
// Sync-type flags and progress numbers for NotifyHistorySqliteChunkApplied /
// Started. Host still owns counters, quiet timer, and enrichment release.
// =============================================================================
using System;

namespace Unison.WhatsAppClient.Helpers
{
    public static class HistorySqliteChunkClassify
    {
        public static void Classify(
            string syncType,
            out string type,
            out bool isOnDemand,
            out bool isFull)
        {
            type = syncType ?? string.Empty;
            isOnDemand = type.IndexOf("OnDemand", StringComparison.OrdinalIgnoreCase) >= 0;
            isFull = type.IndexOf("Full", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static int ClampConversationCount(int conversationCount) =>
            Math.Max(0, conversationCount);

        public static int ProgressTotal(bool isFull, int processed) =>
            isFull ? Math.Max(processed, 1) : 0;

        public static string AppliedProgressReason(bool isFull) =>
            isFull ? "sqlite-full" : "sqlite-chunk";

        public static int FinalizeDelayMs(
            bool isFull,
            int fullFinalizeDelayMs,
            int quietFinalizeDelayMs) =>
            isFull ? fullFinalizeDelayMs : quietFinalizeDelayMs;

        public static bool ShouldCompleteUserResync(int conversationCount, bool isFull) =>
            conversationCount > 0 || isFull;

        public static bool ShouldReleaseEnrichment(
            bool isFull,
            bool enrichmentAwaitingHeavyHistory,
            int conversationCount) =>
            isFull || (enrichmentAwaitingHeavyHistory && conversationCount > 0);

        public static string StartingProgressReason(int conversationCount) =>
            conversationCount > 0 ? "sqlite-starting" : "sqlite-starting-empty";
    }
}
