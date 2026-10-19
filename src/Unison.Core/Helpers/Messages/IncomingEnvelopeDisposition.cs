// =============================================================================
// IncomingEnvelopeDisposition
//
// Early exits on an inbound envelope before (or instead of) a chat row. The
// pump still queues the work; this only names which arm applies so the order
// of those exits cannot drift when the method is split further.
// =============================================================================
using Proto;

namespace Unison.Core.Helpers
{
    public enum IncomingEnvelopeKind
    {
        /// <summary>Keep walking the chat / render path.</summary>
        Continue = 0,

        PeerDataOperationResponse,
        AppStateSessionOnly,
        Placeholder,
        StatusBroadcast,
        Revoke,
        PinInChat
    }

    public static class IncomingEnvelopeDisposition
    {
        /// <summary>
        /// Session-control arms that fire before PN/LID alias registration and
        /// before chat routing.
        /// </summary>
        public static IncomingEnvelopeKind ClassifySessionControl(Message message)
        {
            if (message?.ProtocolMessage?.PeerDataOperationRequestResponseMessage != null)
            {
                return IncomingEnvelopeKind.PeerDataOperationResponse;
            }

            if (message?.ProtocolMessage?.AppStateFatalExceptionNotification != null ||
                message?.ProtocolMessage?.AppStateSyncKeyShare != null)
            {
                return IncomingEnvelopeKind.AppStateSessionOnly;
            }

            if (message?.PlaceholderMessage != null)
            {
                return IncomingEnvelopeKind.Placeholder;
            }

            return IncomingEnvelopeKind.Continue;
        }

        /// <summary>
        /// Status broadcast is an address shape, not a message type — checked after
        /// aliases so LID/PN pairs on the envelope still register.
        /// </summary>
        public static IncomingEnvelopeKind ClassifyAddress(string fromJid)
        {
            if (JidHelper.IsStatusBroadcast(fromJid))
            {
                return IncomingEnvelopeKind.StatusBroadcast;
            }

            return IncomingEnvelopeKind.Continue;
        }

        /// <summary>
        /// Chat-scoped controls that need a resolved conversation JID first.
        /// </summary>
        public static IncomingEnvelopeKind ClassifyChatControl(Message message)
        {
            if (message?.ProtocolMessage != null && (int)message.ProtocolMessage.Type == 0)
            {
                return IncomingEnvelopeKind.Revoke;
            }

            if (message?.PinInChatMessage != null)
            {
                return IncomingEnvelopeKind.PinInChat;
            }

            return IncomingEnvelopeKind.Continue;
        }
    }
}
