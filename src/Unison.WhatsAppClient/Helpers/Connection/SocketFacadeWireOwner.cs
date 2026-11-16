// =============================================================================
// SocketFacadeWireOwner
//
// Owns ISocketFacadeWireHost. Presence / missing / status / receipt / link-code
// façade notifies stay as ctor ports.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.WhatsAppClient.Messages;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class SocketFacadeWireOwner : ISocketFacadeWireHost
    {
        private readonly ReceiptReader _receipts;
        private readonly Action<PresenceUpdateEventArgs> _notifyPresence;
        private readonly Action<string, string, string, bool, DateTime, string> _noteMissing;
        private readonly Func<string, string, string, Task> _notifyOutgoingStatus;
        private readonly Func<ReceiptFacts, Task> _notifyReceipt;

        public SocketFacadeWireOwner(
            IJidResolver jids,
            Action<PresenceUpdateEventArgs> notifyPresence,
            Action<string, string, string, bool, DateTime, string> noteMissing,
            Func<string, string, string, Task> notifyOutgoingStatus,
            Func<ReceiptFacts, Task> notifyReceipt)
        {
            _receipts = new ReceiptReader(jids ?? throw new ArgumentNullException(nameof(jids)));
            _notifyPresence = notifyPresence
                ?? throw new ArgumentNullException(nameof(notifyPresence));
            _noteMissing = noteMissing
                ?? throw new ArgumentNullException(nameof(noteMissing));
            _notifyOutgoingStatus = notifyOutgoingStatus
                ?? throw new ArgumentNullException(nameof(notifyOutgoingStatus));
            _notifyReceipt = notifyReceipt
                ?? throw new ArgumentNullException(nameof(notifyReceipt));
        }

        public void HandlePresence(PresenceUpdateEventArgs args) =>
            SocketFacadeWireDispatch.HandlePresence(this, args);

        public void HandleMissingMessage(
            string chatJid,
            string participant,
            string messageId,
            bool isFromMe,
            DateTime timestamp,
            string reason) =>
            SocketFacadeWireDispatch.HandleMissingMessage(
                this,
                chatJid,
                participant,
                messageId,
                isFromMe,
                timestamp,
                reason);

        public Task HandleOutgoingStatusAsync(string messageId, string status, string error) =>
            SocketFacadeWireDispatch.HandleOutgoingStatusAsync(this, messageId, status, error);

        public Task HandleReceiptAsync(BinaryNode node) =>
            SocketFacadeWireDispatch.HandleReceiptAsync(this, _receipts.Read(node));

        void ISocketFacadeWireHost.NotifyPresenceUpdated(PresenceUpdateEventArgs args) =>
            _notifyPresence(args);

        void ISocketFacadeWireHost.NoteMissingMessageFromWire(
            string chatJid,
            string participant,
            string messageId,
            bool isFromMe,
            DateTime timestamp,
            string reason) =>
            _noteMissing(chatJid, participant, messageId, isFromMe, timestamp, reason);

        Task ISocketFacadeWireHost.NotifyOutgoingStatusAsync(
            string messageId,
            string status,
            string error) =>
            _notifyOutgoingStatus(messageId, status, error);

        Task ISocketFacadeWireHost.NotifyReceiptAsync(ReceiptFacts receipt) =>
            _notifyReceipt(receipt);
    }
}
