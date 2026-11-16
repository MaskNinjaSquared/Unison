// =============================================================================
// IInboundWireNodeHost
//
// Side effects for InboundWireNodeDispatch (OnMessage ack + notify attrs).
// =============================================================================
namespace Unison.WhatsAppClient.Abstractions
{
    public interface IInboundWireNodeHost
    {
        void NotifyInboundWireAck(string ackClass, string ackId, string ackError);

        void NotifyInboundMessageNode(string fromJid, string notify, string participantJid);
    }
}
