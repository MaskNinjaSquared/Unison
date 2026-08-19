using System;

namespace Unison.Core.Models
{
    /// <summary>
    /// A single emoji reaction attached to a <see cref="ChatMessage"/>.
        /// Persisted in SQLite <c>history_message_reaction</c>.
    /// </summary>
    public sealed class MessageReaction
    {
        public string Emoji { get; set; }
        public string ReactorJid { get; set; }
        public string ReactorName { get; set; }
        public DateTime Timestamp { get; set; }
        public string ReactionMessageId { get; set; }
        public bool FromMe { get; set; }
    }
}
