using System;

namespace Unison.Core.Models
{
    /// <summary>
    /// List-row snapshot persisted from a history-sync chunk (SQLite <c>history_chat_preview</c>).
    /// Not full message bodies — enough for the chat list once the UI reads from SQLite (phase 2).
    /// </summary>
    public sealed class HistoryChatPreview
    {
        public string Jid { get; set; }

        public string LidJid { get; set; }

        public string PnJid { get; set; }

        public string Name { get; set; }

        public bool IsGroup { get; set; }

        public int UnreadCount { get; set; }

        public string LastMessage { get; set; }

        /// <summary>Pre-composed prefix (fallback for non-UI readers). UI recomposes from the parts below.</summary>
        public string LastMessageAuthor { get; set; }

        /// <summary>Newest message was ours — drives the localized "You:" strip.</summary>
        public bool LastMessageIsFromMe { get; set; }

        /// <summary>Resolved push name of the group sender (null for 1:1 / own).</summary>
        public string LastMessageSenderName { get; set; }

        /// <summary>Group sender JID, so the strip can fall back to a short label when the name is missing.</summary>
        public string LastMessageParticipantJid { get; set; }

        public ChatPreviewKind LastMessageKind { get; set; }

        /// <summary>Outgoing ticks for the list strip.</summary>
        public MessageSendState LastMessageSendState { get; set; }

        /// <summary>ContextInfo mentioned JIDs for the list-strip @alias parser.</summary>
        public System.Collections.Generic.List<string> LastMessageMentionedJids { get; set; }

        public DateTime? LastMessageTimestampUtc { get; set; }

        /// <summary>MessageId of the tip shown in the list strip (for SQLite reconcile).</summary>
        public string LastMessageId { get; set; }

        /// <summary><see cref="Constants.LocalSettingsConstants.MessageStoreSyncId"/> when written.</summary>
        public string SyncId { get; set; }

        public string SyncType { get; set; }

        public DateTime UpdatedAtUtc { get; set; }
    }
}
