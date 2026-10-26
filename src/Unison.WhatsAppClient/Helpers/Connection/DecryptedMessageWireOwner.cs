// =============================================================================
// DecryptedMessageWireOwner
//
// Owns IDecryptedMessageWireHost. Diagnostics counters + pump enqueue stay as
// ctor ports.
// =============================================================================
using System;
using Proto;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class DecryptedMessageWireOwner : IDecryptedMessageWireHost
    {
        public delegate void EnqueueDecrypted(
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

        private readonly Action _observeDiagnostics;
        private readonly EnqueueDecrypted _enqueue;

        public DecryptedMessageWireOwner(
            Action observeDiagnostics,
            EnqueueDecrypted enqueue)
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
            bool isOffline) =>
            _enqueue(
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
