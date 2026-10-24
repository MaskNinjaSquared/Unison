// =============================================================================
// ISocketFacadeWireHost
//
// Side effects for SocketFacadeWireDispatch (presence / missing / outgoing
// status / receipt / link-code companion — façade forwards from the wire).
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Core.Models;

namespace Unison.Core.Contracts.WhatsApp
{
    public interface ISocketFacadeWireHost
    {
        void NotifyPresenceUpdated(PresenceUpdateEventArgs args);

        void NoteMissingMessageFromWire(
            string chatJid,
            string participant,
            string messageId,
            bool isFromMe,
            DateTime timestamp,
            string reason);

        Task NotifyOutgoingStatusAsync(string messageId, string status, string error);

        Task NotifyReceiptAsync(BinaryNode node);

        void RaiseLinkCodeCompanionReg(BinaryNode node);
    }
}