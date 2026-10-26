// =============================================================================
// IncomingDecryptedNotifyMap
//
// Maps pump envelope fields onto IncomingDecryptedNotifyRequest. The UWP
// DecryptedMessageEventArgs type stays on the host; this only copies scalars.
// =============================================================================
using System;
using Proto;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Helpers
{
    public static class IncomingDecryptedNotifyMap
    {
        public static IncomingDecryptedNotifyRequest FromPump(
            Message message,
            string messageId,
            string fromJid,
            string recipientJid,
            string peerRecipientPn,
            string peerRecipientLid,
            string senderLid,
            string participantJid,
            string participantAltJid,
            string verifiedName,
            string pushName,
            DateTime timestamp,
            bool isOffline,
            bool isFromMe)
        {
            return new IncomingDecryptedNotifyRequest
            {
                Message = message,
                MessageId = messageId,
                FromJid = fromJid,
                RecipientJid = recipientJid,
                PeerRecipientPn = peerRecipientPn,
                PeerRecipientLid = peerRecipientLid,
                SenderLid = senderLid,
                ParticipantJid = participantJid,
                ParticipantAltJid = participantAltJid,
                VerifiedName = verifiedName,
                PushName = pushName,
                Timestamp = timestamp,
                IsOffline = isOffline,
                IsFromMe = isFromMe,
                // MessageFacade fills blank SelfDisplayName from SelfProfileState.
                SelfDisplayName = null
            };
        }
    }
}
