// =============================================================================
// SocketFacadeWireDispatch
//
// Thin façade forwards from WireSocketEventHandlers: presence, missing-message,
// outgoing status, receipt, link-code companion. Handler wiring stays on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Helpers
{
    public static class SocketFacadeWireDispatch
    {
        public static void HandlePresence(
            ISocketFacadeWireHost host,
            PresenceUpdateEventArgs args)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (args == null)
            {
                return;
            }

            host.NotifyPresenceUpdated(args);
        }

        public static void HandleMissingMessage(
            ISocketFacadeWireHost host,
            string chatJid,
            string participant,
            string messageId,
            bool isFromMe,
            DateTime timestamp,
            string reason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            host.NoteMissingMessageFromWire(
                chatJid,
                participant,
                messageId,
                isFromMe,
                timestamp,
                reason);
        }

        public static Task HandleOutgoingStatusAsync(
            ISocketFacadeWireHost host,
            string messageId,
            string status,
            string error)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            return host.NotifyOutgoingStatusAsync(messageId, status, error);
        }

        public static Task HandleReceiptAsync(ISocketFacadeWireHost host, BinaryNode node)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            return host.NotifyReceiptAsync(node);
        }

        public static void HandleLinkCodeCompanion(
            ISocketFacadeWireHost host,
            BinaryNode node,
            bool isCurrentSocket)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (!isCurrentSocket)
            {
                return;
            }

            host.RaiseLinkCodeCompanionReg(node);
        }
    }
}