namespace Unison.Core.Models
{
    /// <summary>Mute/unmute request for list/detail menus. Null <see cref="MutedUntil"/> = unmute.</summary>
    public sealed class ChatMuteRequest
    {
        public ChatItem Chat { get; set; }

        /// <summary>Unix seconds deadline, or null to clear mute.</summary>
        public long? MutedUntil { get; set; }

        public static ChatMuteRequest Mute(ChatItem chat, long mutedUntilUnixSeconds)
        {
            return new ChatMuteRequest { Chat = chat, MutedUntil = mutedUntilUnixSeconds };
        }

        public static ChatMuteRequest Unmute(ChatItem chat)
        {
            return new ChatMuteRequest { Chat = chat, MutedUntil = null };
        }
    }
}
