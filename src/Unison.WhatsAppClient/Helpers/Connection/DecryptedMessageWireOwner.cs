// =============================================================================
// DecryptedMessageWireOwner
//
// Owns IDecryptedMessageWireHost: maps the wire envelope onto the pump request.
// Diagnostics counters + pump enqueue stay as ctor ports.
// =============================================================================
using System;
using Proto;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Messages;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class DecryptedMessageWireOwner : IDecryptedMessageWireHost
    {
        private readonly Action _observeDiagnostics;
        private readonly Action<IncomingDecryptedNotifyRequest> _enqueue;

        public DecryptedMessageWireOwner(
            Action observeDiagnostics,
            Action<IncomingDecryptedNotifyRequest> enqueue)
        {
            _observeDiagnostics = observeDiagnostics
                ?? throw new ArgumentNullException(nameof(observeDiagnostics));
            _enqueue = enqueue ?? throw new ArgumentNullException(nameof(enqueue));
        }

        public void Handle(
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
            bool isOffline) =>
            DecryptedMessageWireDispatch.Handle(
                this,
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

        void IDecryptedMessageWireHost.ObserveDecryptedDiagnostics() =>
            _observeDiagnostics();

        void IDecryptedMessageWireHost.EnqueueDecryptedFromWire(
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
            // addressingMode is wire-only diagnostics today; the pump envelope omits it.
            _ = addressingMode;
            _enqueue(
                IncomingDecryptedNotifyMap.FromPump(
                    message,
                    messageId,
                    fromJid,
                    recipientJid,
                    peerRecipientPn,
                    peerRecipientLid,
                    senderLid,
                    participant,
                    participantAlt,
                    verifiedName,
                    pushName,
                    timestamp,
                    isOffline,
                    isFromMe));
        }
    }
}
