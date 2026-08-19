using System;
using SQLite;

namespace Unison.Uwp.Data.Entities
{
    /// <summary>
    /// One emoji per reactor on a history message (<c>history_message_reaction</c>).
    /// PK = ChatJid + MessageId + ReactorJid.
    /// </summary>
    [Table("history_message_reaction")]
    public sealed class HistoryMessageReactionRow
    {
        [PrimaryKey]
        public string Id { get; set; }

        [Indexed(Name = "ix_hmr_parent", Order = 1)]
        public string ChatJid { get; set; }

        [Indexed(Name = "ix_hmr_parent", Order = 2)]
        public string MessageId { get; set; }

        public string ReactorJid { get; set; }

        public string ReactorName { get; set; }

        public string Emoji { get; set; }

        public bool FromMe { get; set; }

        public string ReactionMessageId { get; set; }

        public DateTime TimestampUtc { get; set; }

        public static string MakeId(string chatJid, string messageId, string reactorJid)
        {
            return (chatJid ?? string.Empty) + "\u001f" +
                   (messageId ?? string.Empty) + "\u001f" +
                   (reactorJid ?? string.Empty);
        }
    }
}
