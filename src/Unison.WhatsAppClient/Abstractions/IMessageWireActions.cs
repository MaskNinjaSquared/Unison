// =============================================================================
// IMessageWireActions
//
// Presence subscribe and pin-in-chat that today go through the UWP bridge socket.
// Portable MessageFacade calls this port; the adapter forwards to IWhatsAppSocket.
// =============================================================================
using System.Threading.Tasks;
using Proto;

namespace Unison.WhatsAppClient.Abstractions
{
    /// <summary>Wire-side message actions that are not covered by IOutgoingMessageHost.</summary>
    public interface IMessageWireActions
    {
        /// <summary>Null / disconnected socket — presence subscribe is a no-op.</summary>
        bool CanSubscribePresence { get; }

        /// <summary>Null socket — pin must fail rather than pretend it was sent.</summary>
        bool CanSendPin { get; }

        Task PresenceSubscribeAsync(string jid);

        Task SendPinInChatMessageAsync(
            string chatJid,
            MessageKey targetKey,
            bool pin,
            uint durationSeconds);
    }
}
