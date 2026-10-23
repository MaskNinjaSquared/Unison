using System;
using Proto;

namespace Unison.Core.Models
{
    /// <summary>
    /// Envelope fields for the post-routing inbound content path: render, push-name,
    /// sender, attention, build <see cref="ChatMessage"/>, accept timeline, follow-up.
    /// </summary>
    public sealed class IncomingChatContentProcessRequest
    {
        public Message Message { get; set; }
        public string MessageId { get; set; }
        public string ChatJid { get; set; }
        public string FromJid { get; set; }
        public string ParticipantJid { get; set; }
        public string ParticipantAltJid { get; set; }
        public string VerifiedName { get; set; }
        public string PushName { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsOffline { get; set; }
        public bool IsGroup { get; set; }
        public bool EnvelopeIsFromMe { get; set; }
        public string SelfDisplayName { get; set; }
        public bool QuietForSync { get; set; }
        public bool IsActiveChat { get; set; }
        public bool IsWindowVisible { get; set; }
    }
}
