// =============================================================================
// IDecryptedMessageWireHost
//
// Side effects for DecryptedMessageWireDispatch (OnDecryptedMessageReceived).
// =============================================================================
using System;
using Proto;

namespace Unison.Core.Contracts.WhatsApp
{
    public interface IDecryptedMessageWireHost
    {
        void ObserveDecryptedDiagnostics();

        void EnqueueDecryptedFromWire(
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
            bool isOffline);
    }
}
