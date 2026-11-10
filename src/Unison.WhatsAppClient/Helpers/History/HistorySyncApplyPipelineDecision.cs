// =============================================================================
// HistorySyncApplyPipelineDecision
//
// Pure rules for the serialized history-chunk apply pipe (HistorySyncWireApply →
// PersistHistorySqliteChunkAsync). Conversation-bearing chunks paint the list;
// LID-directory chunks (PushName / empty NonBlocking) must not starve them or
// double-write aliases/people already covered by the socket LidMappingStore +
// Pushnames list — especially on Unsupported (185 MB) first sync.
// =============================================================================
using System;

namespace Unison.WhatsAppClient.Helpers
{
    public static class HistorySyncApplyPipelineDecision
    {
        /// <summary>
        /// Lower class number = higher apply priority (dequeued first).
        /// </summary>
        public const int PriorityConversationBearing = 0;
        public const int PriorityOtherEmpty = 1;
        public const int PriorityLidDirectory = 2;

        /// <summary>
        /// Chunk carries conversation rows the SQLite/list path must persist.
        /// </summary>
        public static bool IsConversationBearing(int conversationCount)
        {
            return conversationCount > 0;
        }

        /// <summary>
        /// Directory / mapping wave with no conversations: PushName and empty
        /// NonBlockingData. Socket <c>StoreMappingsAsync</c> already owns LID
        /// pairs; Pushnames own display names.
        /// </summary>
        public static bool IsLidDirectoryChunk(string syncType, int conversationCount)
        {
            if (IsConversationBearing(conversationCount))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(syncType))
            {
                return false;
            }

            if (syncType.IndexOf("PushName", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return syncType.IndexOf("NonBlocking", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static int ApplyPriorityClass(string syncType, int conversationCount)
        {
            if (IsConversationBearing(conversationCount))
            {
                return PriorityConversationBearing;
            }

            if (IsLidDirectoryChunk(syncType, conversationCount))
            {
                return PriorityLidDirectory;
            }

            return PriorityOtherEmpty;
        }

        /// <summary>
        /// Sort key for the apply queue: lower class first, then FIFO by sequence.
        /// </summary>
        public static int CompareApplyOrder(
            string syncTypeA,
            int conversationsA,
            long sequenceA,
            string syncTypeB,
            int conversationsB,
            long sequenceB)
        {
            int classCompare = ApplyPriorityClass(syncTypeA, conversationsA)
                .CompareTo(ApplyPriorityClass(syncTypeB, conversationsB));
            if (classCompare != 0)
            {
                return classCompare;
            }

            return sequenceA.CompareTo(sequenceB);
        }

        /// <summary>
        /// Facade must not re-register <c>PhoneNumberToLidMappings</c> into the
        /// alias table for LID-directory chunks: the socket handler already
        /// stored them for decrypt, and a second bulk import storms Mobile RAM.
        /// Conversation-bearing chunks still register so list paint can resolve
        /// LID↔PN while rows materialize.
        /// </summary>
        public static bool ShouldRegisterPhoneNumberLidAliases(
            string syncType,
            int conversationCount)
        {
            return !IsLidDirectoryChunk(syncType, conversationCount);
        }

        /// <summary>
        /// Harvest <c>Conversation.LidJid</c>/<c>PnJid</c> into the alias table
        /// only when conversations are present.
        /// </summary>
        public static bool ShouldHarvestConversationLidAliases(int conversationCount)
        {
            return IsConversationBearing(conversationCount);
        }

        /// <summary>
        /// Mirroring push names onto LID JIDs via person upsert for every
        /// PhoneNumberToLidMappings row doubles SQLite writes on PushName
        /// (dump: ~1000 pairs / ~45s). Pushnames already carry the names.
        /// </summary>
        public static bool ShouldUpsertPeopleFromPhoneNumberLidMappings(
            string syncType,
            int conversationCount)
        {
            return !IsLidDirectoryChunk(syncType, conversationCount);
        }

        /// <summary>
        /// Tip reconcile needs conversation or message JIDs; skip the hop on
        /// empty directory chunks.
        /// </summary>
        public static bool ShouldRunHistoryChunkTipReconcile(int conversationCount)
        {
            return IsConversationBearing(conversationCount);
        }
    }
}
