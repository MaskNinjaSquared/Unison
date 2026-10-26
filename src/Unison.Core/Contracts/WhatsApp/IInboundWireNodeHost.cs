// =============================================================================
// IInboundWireNodeHost
//
// Side effects for InboundWireNodeDispatch (OnMessage ack + notify attrs).
// =============================================================================
namespace Unison.Core.Contracts.WhatsApp
{
    public interface IInboundWireNodeHost
    {
        void NotifyInboundWireAck(string ackClass, string ackId, string ackError);

        void NotifyInboundMessageNode(string fromJid, string notify, string participantJid);
    }
}
