// =============================================================================
// InboundWireNodeOwner
//
// Owns IInboundWireNodeHost. Ack → message façade and notify → contact façade
// stay as ctor ports.
// =============================================================================
using System;
using Unison.Baileys.Protocol;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class InboundWireNodeOwner : IInboundWireNodeHost
    {
        private readonly Action<string, string, string> _notifyAck;
        private readonly Action<string, string, string> _notifyMessageNode;

        public InboundWireNodeOwner(
            Action<string, string, string> notifyAck,
            Action<string, string, string> notifyMessageNode)
        {
            _notifyAck = notifyAck ?? throw new ArgumentNullException(nameof(notifyAck));
            _notifyMessageNode = notifyMessageNode
                ?? throw new ArgumentNullException(nameof(notifyMessageNode));
        }

        public void Handle(BinaryNode node) =>
            InboundWireNodeDispatch.Handle(this, node);

        void IInboundWireNodeHost.NotifyInboundWireAck(
            string ackClass,
            string ackId,
            string ackError) =>
            _notifyAck(ackClass, ackId, ackError);

        void IInboundWireNodeHost.NotifyInboundMessageNode(
            string fromJid,
            string notify,
            string participantJid) =>
            _notifyMessageNode(fromJid, notify, participantJid);
    }
}
