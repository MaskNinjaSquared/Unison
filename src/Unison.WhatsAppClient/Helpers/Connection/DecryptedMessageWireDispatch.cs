// =============================================================================
// DecryptedMessageWireDispatch
//
// OnDecryptedMessageReceived body: diagnostics observe then enqueue. Handler
// wiring stays on the host.
// =============================================================================
using System;
using Proto;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    public static class DecryptedMessageWireDispatch
    {
        public static void Handle(
            IDecryptedMessageWireHost host,
            string fromJid,
            string participant,
            string participantAlt,
            string addressingMode,
            string messageId,
            Message message,
            DateTime timestamp,
            bool isFromMe,
            string pushName,
            string verifiedName,
            string senderLid,
            string peerRecipientPn,
            string peerRecipientLid,
            string recipientJid,
            bool isOffline)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            host.ObserveDecryptedDiagnostics();
            host.EnqueueDecryptedFromWire(
                fromJid,
                participant,
                participantAlt,
                addressingMode,
                messageId,
                message,
                timestamp,
                isFromMe,
                pushName,
                verifiedName,
                senderLid,
                peerRecipientPn,
                peerRecipientLid,
                recipientJid,
                isOffline);
        }
    }
}
