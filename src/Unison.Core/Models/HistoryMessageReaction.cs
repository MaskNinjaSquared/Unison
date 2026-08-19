using System;

namespace Unison.Core.Models
{
    /// <summary>
    /// One reactor's emoji on a history message (SQLite <c>history_message_reaction</c>).
    /// Empty <see cref="Emoji"/> means delete that reactor's row.
    /// </summary>
    public sealed class HistoryMessageReaction
    {
        public string ChatJid { get; set; }

        public string MessageId { get; set; }

        public string ReactorJid { get; set; }

        public string ReactorName { get; set; }

        public string Emoji { get; set; }

        public bool FromMe { get; set; }

        public string ReactionMessageId { get; set; }

        public DateTime TimestampUtc { get; set; }
    }
}
