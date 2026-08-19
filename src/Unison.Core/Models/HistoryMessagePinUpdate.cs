using System;

namespace Unison.Core.Models
{
    /// <summary>Pin / unpin applied onto an existing <c>history_message</c> row.</summary>
    public sealed class HistoryMessagePinUpdate
    {
        public string ChatJid { get; set; }

        public string MessageId { get; set; }

        public bool IsPinned { get; set; }

        public DateTime? PinnedAtUtc { get; set; }

        public DateTime? PinExpiresAtUtc { get; set; }
    }
}
