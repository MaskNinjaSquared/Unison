using System;
using Proto;

namespace Unison.Core.Models
{
    /// <summary>
    /// Inbound envelope after session-only / placeholder handling: status, offline fast-path,
    /// DM routing, revoke/pin/reaction, then chat content.
    /// </summary>
    public sealed class IncomingRoutedEnvelopeRequest
    {
        public Message Message { get; set; }
        public string MessageId { get; set; }
        public string FromJid { get; set; }
        public string RecipientJid { get; set; }
        public string PeerRecipientPn { get; set; }
        public string PeerRecipientLid { get; set; }
        public string SenderLid { get; set; }
        public string ParticipantJid { get; set; }
        public string ParticipantAltJid { get; set; }
        public string VerifiedName { get; set; }
        public string PushName { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsOffline { get; set; }
        public bool IsFromMe { get; set; }
        public string SelfDisplayName { get; set; }
        public bool QuietForSync { get; set; }
        public bool IsWindowVisible { get; set; }
        public Func<string, bool> IsActiveChat { get; set; }
    }
}
