// =============================================================================
// ChatUpdate
//
// A partial chat: only the fields that actually changed.
//
// Every property is nullable and null means "untouched", which is the whole
// point - a mute notification must not clear the unread count just because it
// does not mention it. Baileys expresses this with Partial<Chat>; C# needs the
// nullables to say the same thing.
//
// Ports: rc14 Chat in src/Types/Chat.ts, as emitted by chats.update
// =============================================================================
namespace Unison.Socket.Models
{
    public sealed class ChatUpdate
    {
        public ChatUpdate()
        {
        }

        public ChatUpdate(string id)
        {
            Id = id;
        }

        /// <summary>The chat JID. Always set; it is what the update applies to.</summary>
        public string Id { get; set; }

        public string Name { get; set; }

        public int? UnreadCount { get; set; }

        /// <summary>Unix seconds of the newest message, used for ordering the list.</summary>
        public long? ConversationTimestamp { get; set; }

        /// <summary>Unix seconds when the mute expires, or 0 to unmute.</summary>
        public long? MuteEndTime { get; set; }

        public bool? Archived { get; set; }

        /// <summary>Sort key for a pinned chat; 0 unpins.</summary>
        public long? Pinned { get; set; }

        public bool? MarkedAsUnread { get; set; }

        /// <summary>True for chats the user cannot write to, such as an announcement group.</summary>
        public bool? ReadOnly { get; set; }

        /// <summary>Disappearing-message duration in seconds; 0 turns it off.</summary>
        public int? EphemeralExpiration { get; set; }

        public long? EphemeralSettingTimestamp { get; set; }

        /// <summary>
        /// The privacy token the server hands us for this contact. It has to be echoed back when
        /// messaging them, so it is carried through as opaque bytes.
        /// </summary>
        public byte[] TcToken { get; set; }

        public long? TcTokenTimestamp { get; set; }

        public long? TcTokenSenderTimestamp { get; set; }
    }
}
