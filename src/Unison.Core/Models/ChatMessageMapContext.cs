using System;

namespace Unison.Core.Models
{
    /// <summary>
    /// Context for mapping a proto payload into <see cref="ChatMessage"/> / <see cref="PendingReaction"/>.
    /// </summary>
    public sealed class ChatMessageMapContext
    {
        public string MessageId { get; set; }
        public string ChatJid { get; set; }
        public string RemoteJid { get; set; }
        public string ParticipantJid { get; set; }
        public string SenderName { get; set; }
        public bool IsFromMe { get; set; }
        public DateTime Timestamp { get; set; }
        public string Status { get; set; }
        public bool IsPinned { get; set; }
        public DateTime? PinnedAtUtc { get; set; }
        public DateTime? PinExpiresAtUtc { get; set; }
    }
}
