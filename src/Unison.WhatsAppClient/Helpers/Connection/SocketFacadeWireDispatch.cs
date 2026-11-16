// =============================================================================
// SocketFacadeWireDispatch
//
// Thin façade forwards from WireSocketEventHandlers: presence, missing-message,
// outgoing status, receipt, link-code companion. Handler wiring stays on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
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

        /// <summary>Null facts = the reader found nothing to act on (retry, unknown type).</summary>
        public static Task HandleReceiptAsync(ISocketFacadeWireHost host, ReceiptFacts receipt)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            return receipt == null ? Task.CompletedTask : host.NotifyReceiptAsync(receipt);
        }
    }
}