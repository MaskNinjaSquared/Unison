// =============================================================================
// AppStatePinMapping
//
// Baileys 7.0.0-rc14 processSyncAction: pinAction.pinned → pinned = timestamp,
// otherwise null. SyncActionValue.timestamp is often ms; Conversation.pinned is
// uint32 seconds. A missing timestamp defaults to 0 in protobuf — that must not
// be collapsed into "unpinned" (host used to test Pinned > 0).
// =============================================================================
namespace Unison.Core.Helpers
{
    public static class AppStatePinMapping
    {
        public struct Result
        {
            public bool AppliesPin;
            public bool IsPinned;
            public long PinnedTimestampUnixSeconds;
        }

        public static Result FromAction(bool pinned, long actionTimestamp, long fallbackTimestampMs)
        {
            if (!pinned)
            {
                return new Result
                {
                    AppliesPin = true,
                    IsPinned = false,
                    PinnedTimestampUnixSeconds = 0
                };
            }

            long raw = actionTimestamp > 0 ? actionTimestamp : fallbackTimestampMs;
            if (raw <= 0)
            {
                raw = 1;
            }

            return new Result
            {
                AppliesPin = true,
                IsPinned = true,
                PinnedTimestampUnixSeconds = NormalizeSortKey(raw) ?? 1
            };
        }

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
