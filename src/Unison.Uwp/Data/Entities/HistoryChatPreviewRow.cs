using System;
using SQLite;
using Unison.Core.Models;

namespace Unison.Uwp.Data.Entities
{
    /// <summary>
    /// List-preview row from history sync (<c>history_chat_preview</c>).
    /// </summary>
    [Table("history_chat_preview")]
    public sealed class HistoryChatPreviewRow
    {
        [PrimaryKey]
        public string Jid { get; set; }

        public string LidJid { get; set; }

        public string PnJid { get; set; }

        public string Name { get; set; }

        public bool IsGroup { get; set; }

        public int UnreadCount { get; set; }

        public string LastMessage { get; set; }

        public string LastMessageAuthor { get; set; }

        public bool LastMessageIsFromMe { get; set; }

        public string LastMessageSenderName { get; set; }

        public string LastMessageParticipantJid { get; set; }

        /// <summary><see cref="ChatPreviewKind"/> as INTEGER.</summary>
        public int LastMessageKind { get; set; }

        /// <summary><see cref="MessageSendState"/> as INTEGER.</summary>
        public int LastMessageSendState { get; set; }

        /// <summary>Comma-separated mentioned JIDs for the list strip.</summary>
        public string LastMessageMentionedJids { get; set; }

        /// <summary>MessageId of the tip shown on the list strip.</summary>
        public string LastMessageId { get; set; }

        public DateTime? LastMessageTimestampUtc { get; set; }

        public string SyncId { get; set; }

        public string SyncType { get; set; }

        public DateTime UpdatedAtUtc { get; set; }
    }
}
