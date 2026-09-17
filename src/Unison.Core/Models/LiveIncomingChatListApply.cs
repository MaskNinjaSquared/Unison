// =============================================================================
// LiveIncomingChatListApply
//
// Facts the chat façade needs to update the list strip for one live inbound
// message: preview, unread bump, and the ChatItem that feeds the toast.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Mappers;

namespace Unison.Core.Models
{
    public sealed class LiveIncomingChatListApplyRequest
    {
        public string ChatJid { get; set; }
        public string PreviewText { get; set; }
        public DateTime Timestamp { get; set; }
        public ChatPreviewKind PreviewKind { get; set; }
        public string AuthorPrefix { get; set; }
        public IList<string> MentionedJids { get; set; }
        public bool IsFromMe { get; set; }
        public MessageSendState SendState { get; set; }
        public string MessageId { get; set; }

        /// <summary>
        /// How many unread increments to apply. Live uses 0 or 1; offline replay uses the
        /// delta recorded at drain time (never re-ask IncomingAttention on apply).
        /// </summary>
        public int UnreadDelta { get; set; }

        /// <summary>Legacy live flag; prefer <see cref="UnreadDelta"/>. Mapped as 1 when delta is 0.</summary>
        public bool CountsAsUnread { get; set; }

        public bool IsGroup { get; set; }

        /// <summary>Optional LID side of a known PN/LID pair for merge scan after creating a row.</summary>
        public string AliasLid { get; set; }

        /// <summary>Optional PN side of a known PN/LID pair for merge scan after creating a row.</summary>
        public string AliasPn { get; set; }
    }

    public sealed class LiveIncomingChatListApplyResult
    {
        public ChatItem Chat { get; set; }
        public int TotalUnread { get; set; }
        public string DisplayName { get; set; }
    }
}
