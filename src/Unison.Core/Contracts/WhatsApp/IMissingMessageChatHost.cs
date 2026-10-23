using System;
using System.Threading.Tasks;
using Unison.Core.State;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Placeholder-resend ledger half that still lives on the client: note / resolve candidates,
    /// ask the socket for a resend, and drop a conversation's pending repairs when the row goes away.
    /// </summary>
    public interface IMissingMessageChatHost
    {
        void ForgetMissingMessagesForChat(string chatJid);

        /// <summary>
        /// Clears one pending repair when the message arrives (live, duplicate, or empty skip).
        /// </summary>
        void ResolveMissingMessage(string chatJid, string messageId, string source);

        /// <summary>
        /// Records a placeholder / missing-message candidate for later resend.
        /// </summary>
        void RegisterMissingMessage(
            string chatJid,
            string participant,
            string messageId,
            bool isFromMe,
            DateTime timestamp,
            string reason);

        /// <summary>
        /// Asks the live socket for a placeholder resend when the ledger still holds the id
        /// and reconnect / sync deferral allows it.
        /// </summary>
        Task<bool> RequestPlaceholderResendAsync(string chatJid, string messageId, string trigger);

        /// <summary>
        /// Claims the outstanding placeholder-resend request for a peer-data stanza, if any.
        /// </summary>
        PlaceholderResendRequestState TakePlaceholderPeerResponse(string stanzaId);
    }
}
