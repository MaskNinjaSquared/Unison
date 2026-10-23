using System;
using Proto;

namespace Unison.Core.Models
{
    /// <summary>
    /// Raw decrypted envelope fields from the ingest pump. Quiet-for-sync / window / active-chat
    /// context is filled by <see cref="Contracts.WhatsApp.IMessageService.NotifyIncomingDecryptedAsync"/>.
    /// </summary>
    public sealed class IncomingDecryptedNotifyRequest
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
    }
}
