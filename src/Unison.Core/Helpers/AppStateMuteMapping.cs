// =============================================================================
// AppStateMuteMapping
//
// Baileys 7.0.0-rc14 processSyncAction: muteAction.muted → muteEndTime timestamp,
// otherwise null. ChatItem stores unix seconds; 0 means forever. Emitting 0 for
// unmute therefore looked like forever and left the mute icon stuck after sync.
// =============================================================================
namespace Unison.Core.Helpers
{
    public static class AppStateMuteMapping
    {
        public struct Result
        {
            public bool AppliesMute;
            public long? MutedUntilUnixSeconds;
        }

        public static Result FromAction(bool muted, long muteEndTimestamp)
        {
            if (!muted)
            {
                return new Result
                {
                    AppliesMute = true,
                    MutedUntilUnixSeconds = null
                };
            }

            return new Result
            {
                AppliesMute = true,
                MutedUntilUnixSeconds = muteEndTimestamp <= 0
                    ? 0
                    : HistoryConversationFlagsReader.ToUnixSeconds(
                        unchecked((ulong)muteEndTimestamp))
            };
        }
    }
}
