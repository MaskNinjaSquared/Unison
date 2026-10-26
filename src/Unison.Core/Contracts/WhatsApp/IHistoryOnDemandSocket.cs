using System.Threading.Tasks;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>The socket calls an on-demand history request makes. The sends may throw when the socket is gone.</summary>
    public interface IHistoryOnDemandSocket
    {
        /// <summary>Handshake complete; nothing is asked of the phone before that.</summary>
        bool IsReady { get; }

        string GenerateMessageId();

        /// <summary>
        /// Asks for the page of <paramref name="chatJid"/> older than the given message, under
        /// <paramref name="stanzaId"/>, and returns the id it went out with.
        /// </summary>
        Task<string> RequestChatHistoryAsync(
            string chatJid,
            string oldestMessageId,
            bool oldestFromMe,
            long oldestTimestampMs,
            int count,
            string stanzaId);

        /// <summary>
        /// Asks for the account-wide catch-up under <paramref name="stanzaId"/>; returns the id
        /// it went out with. <paramref name="requestId"/> correlates chunks on the session bus
        /// when the caller waits for an answer (resync), rather than for a timer.
        /// </summary>
        Task<string> RequestFullHistoryAsync(string stanzaId, string requestId = null);
    }
}

