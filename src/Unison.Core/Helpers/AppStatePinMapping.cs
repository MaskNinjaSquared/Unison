// =============================================================================
// AppStatePinMapping
//
// Pin sort keys arrive as SyncActionValue.timestamp (usually ms) or as
// Conversation.pinned (uint32 seconds). Normalize to unix seconds so list order
// does not prefer whichever source used the larger unit.
// =============================================================================
namespace Unison.Core.Helpers
{
    public static class AppStatePinMapping
    {
        public static long? NormalizeSortKey(long? raw)
        {
            if (!raw.HasValue)
            {
                return null;
            }

            if (raw.Value <= 0)
            {
                return 0;
            }

            return HistoryConversationFlagsReader.ToUnixSeconds(unchecked((ulong)raw.Value));
        }
    }
}
