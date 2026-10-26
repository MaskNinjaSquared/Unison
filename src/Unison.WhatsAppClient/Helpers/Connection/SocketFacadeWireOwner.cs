// =============================================================================
// SocketFacadeWireOwner
//
// Owns ISocketFacadeWireHost. Presence / missing / status / receipt / link-code
// façade notifies stay as ctor ports.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class SocketFacadeWireOwner : ISocketFacadeWireHost
    {
        private readonly Action<PresenceUpdateEventArgs> _notifyPresence;
        private readonly Action<string, string, string, bool, DateTime, string> _noteMissing;
        private readonly Func<string, string, string, Task> _notifyOutgoingStatus;
        private readonly Func<BinaryNode, Task> _notifyReceipt;
        private readonly Action<BinaryNode> _raiseLinkCode;

        public SocketFacadeWireOwner(
            Action<PresenceUpdateEventArgs> notifyPresence,
            Action<string, string, string, bool, DateTime, string> noteMissing,
            Func<string, string, string, Task> notifyOutgoingStatus,
            Func<BinaryNode, Task> notifyReceipt,
            Action<BinaryNode> raiseLinkCode)
        {
            _notifyPresence = notifyPresence
                ?? throw new ArgumentNullException(nameof(notifyPresence));
            _noteMissing = noteMissing
                ?? throw new ArgumentNullException(nameof(noteMissing));
            _notifyOutgoingStatus = notifyOutgoingStatus
                ?? throw new ArgumentNullException(nameof(notifyOutgoingStatus));
            _notifyReceipt = notifyReceipt
                ?? throw new ArgumentNullException(nameof(notifyReceipt));
            _raiseLinkCode = raiseLinkCode
                ?? throw new ArgumentNullException(nameof(raiseLinkCode));
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
            SocketFacadeWireDispatch.HandleReceiptAsync(this, node);

        public void HandleLinkCodeCompanion(BinaryNode node, bool isCurrentSocket) =>
            SocketFacadeWireDispatch.HandleLinkCodeCompanion(this, node, isCurrentSocket);

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

        Task ISocketFacadeWireHost.NotifyReceiptAsync(BinaryNode node) =>
            _notifyReceipt(node);

        void ISocketFacadeWireHost.RaiseLinkCodeCompanionReg(BinaryNode node) =>
            _raiseLinkCode(node);
    }
}
