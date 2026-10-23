using System;
using System.Threading.Tasks;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Local half of account-driven list/timeline mutations: write the in-memory rows and
    /// the durable mirror without telling the server.
    /// </summary>
    /// <remarks>
    /// The wire half lives on the façades (<c>IChatService.SetPinnedAsync</c>,
    /// <c>DeleteChatAsync</c>, <c>IMessageService.SetMessagePinnedAsync</c>). The client still
    /// owns the row surgery and the SQLite/catalog writes those paths share with app-state
    /// sync from the phone.
    /// </remarks>
    public interface IAppStateLocalHost
    {
        /// <summary>Writes the chat-list pin onto the rows and the local mirror.</summary>
        Task ApplyChatPinAsync(string jid, bool pinned);

        /// <summary>
        /// Account-side archive / pin / mute onto rows and durable mirrors.
        /// Null fields are left alone — an absent mute must not clear an archive.
        /// </summary>
        Task ApplyChatFlagsAsync(
            string jid,
            bool? archived = null,
            bool? pinned = null,
            long? muteEndTimestamp = null,
            long? pinnedTimestamp = null,
            bool applyMute = false);

        /// <summary>
        /// Account-side read/unread onto every alias row of the conversation, then badge / slice.
        /// </summary>
        Task ApplyChatReadStateAsync(string jid, bool read);

        /// <summary>
        /// Removes the conversation from the list, the in-memory timeline and local storage.
        /// </summary>
        Task ApplyChatDeletionAsync(string jid);

        /// <summary>Writes a message pin onto the timeline and local storage.</summary>
        Task ApplyMessagePinAsync(
            string chatJid,
            string messageId,
            bool isPinned,
            DateTime? pinnedAtUtc,
            DateTime? expiresAtUtc);
    }
}
