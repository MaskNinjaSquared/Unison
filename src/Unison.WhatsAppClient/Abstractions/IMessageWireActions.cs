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

        /// <summary>Null socket — delete-for-me patch cannot be sent.</summary>
        bool CanSendDeleteForMe { get; }

        /// <summary>Null socket — delete-for-everyone revoke cannot be sent.</summary>
        bool CanSendDeleteForEveryone { get; }

        Task PresenceSubscribeAsync(string jid);

        Task SendPinInChatMessageAsync(
            string chatJid,
            MessageKey targetKey,
            bool pin,
            uint durationSeconds);

        /// <summary>
        /// Account-side delete-for-me app-state patch (Baileys chatModify deleteForMe).
        /// <paramref name="messageTimestampUnixSeconds"/> is WA message time in Unix seconds.
        /// </summary>
        Task DeleteMessageForMeAsync(
            string chatJid,
            string messageId,
            bool fromMe,
            long messageTimestampUnixSeconds,
            bool deleteMedia);

        /// <summary>Protocol revoke (delete for everyone) naming the target message key.</summary>
        Task SendDeleteForEveryoneAsync(string chatJid, MessageKey targetKey);
    }
}
