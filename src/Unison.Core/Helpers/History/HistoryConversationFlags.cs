// =============================================================================
// HistoryConversationFlags
//
// Pin and mute as they arrive on a history-sync Conversation. The preview
// builder used to read the subject, the unread count and the archived bit, and
// leave these two on the floor — so a conversation that was pinned or muted on
// the phone showed neither icon after the sync, and the durable ChatStore never
// learned about them either. App-state can carry the same facts later, but on a
// fresh link the history chunk is what we have, and "later" is often never.
// =============================================================================
using Proto;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// The pin / mute flags one history-sync conversation carries. Absent means
    /// the chunk did not speak about that flag — not that it was turned off.
    /// </summary>
    public struct HistoryConversationFlags
    {
        public bool? Pinned;
        public long? PinnedTimestamp;
        public bool AppliesMute;
        public long? MutedUntil;
    }

    public static class HistoryConversationFlagsReader
    {
        /// <summary>
        /// Reads pin and mute off a <see cref="Conversation"/>. Returns a change
        /// that touches nothing when the conversation is null or carries neither.
        /// </summary>
        public static HistoryConversationFlags Read(Conversation conv)
        {
            var flags = new HistoryConversationFlags();
            if (conv == null)
            {
                return flags;
            }

            if (conv.HasPinned)
            {
                // Same convention as ChatUpdate / app-state: a positive timestamp is
                // pinned, zero is an explicit unpin.
                flags.Pinned = conv.Pinned > 0;
                flags.PinnedTimestamp = conv.Pinned > 0 ? (long?)conv.Pinned : 0;
            }

            if (conv.HasMuteEndTime)
            {
                flags.AppliesMute = true;
                // 0 is WhatsApp's "forever". Anything else is a deadline, and the
                // proto has historically mixed milliseconds with seconds — ChatItem
                // stores unix seconds, so convert when the value looks like ms.
                flags.MutedUntil = ToUnixSeconds(conv.MuteEndTime);
            }

            return flags;
        }

        /// <summary>
        /// Converts a Conversation / SyncAction mute deadline into unix seconds.
        /// </summary>
        public static long ToUnixSeconds(ulong raw)
        {
            if (raw == 0)
            {
                return 0;
            }

            // 10 billion seconds is year 2286; timestamps at or above that are ms.
            if (raw >= 10_000_000_000UL)
            {
                return (long)(raw / 1000UL);
            }

            return (long)raw;
        }

        /// <summary>
        /// Projects onto the same shape <see cref="AppStateChatMutation.ApplyFlags"/> consumes.
        /// </summary>
        public static ChatFlagChange ToChatFlagChange(HistoryConversationFlags flags)
        {
            return new ChatFlagChange
            {
                Pinned = flags.Pinned,
                PinnedTimestamp = flags.PinnedTimestamp,
                AppliesMute = flags.AppliesMute,
                MuteEndTimestamp = flags.MutedUntil
            };
        }
    }
}
