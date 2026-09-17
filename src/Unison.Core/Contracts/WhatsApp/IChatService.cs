// =============================================================================
// IChatService
//
// What the user does to a conversation as a whole, as opposed to what they do
// inside one.
//
// Pinning and marking read are account-wide facts, not local preferences: the
// phone and every other linked device are meant to agree, so both of these go
// out on the wire and come back as app state. The local copy is kept anyway,
// because the list has to sort and draw before any of that round trips - and
// has to keep working with no connection at all.
// =============================================================================
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts.WhatsApp
{
    public interface IChatService
    {
        /// <summary>
        /// Pins the conversation to the top of the list, for this account everywhere. Distinct
        /// from <see cref="ChatItem.IsWidgetPinned"/>, which is a tile on this device's Start
        /// screen and means nothing to WhatsApp.
        /// </summary>
        Task SetPinnedAsync(ChatItem chat, bool pinned);

        /// <summary>
        /// Marks everything in the conversation as read: the senders are told, and the unread
        /// badge is cleared here and on the phone. Does nothing when there was nothing unread,
        /// so it is safe to call every time a chat is opened.
        /// </summary>
        Task MarkReadAsync(ChatItem chat);

        /// <summary>
        /// Deletes the conversation for this account everywhere: it leaves the list here, on the
        /// phone and on every other linked device. The messages are removed locally too - unlike a
        /// pin there is nothing to revert to, so this is not undoable and callers are expected to
        /// have asked first.
        /// </summary>
        Task DeleteChatAsync(ChatItem chat);

        /// <summary>
        /// Which conversation is on screen. The notification path reads it to stay quiet about the
        /// chat the user is already looking at; null means none.
        /// </summary>
        void SetActiveChatJid(string jid);

        /// <summary>
        /// Zeroes the unread count locally, on every row that is the same conversation. PN and LID
        /// aliases can briefly produce more than one row, and a leftover alias is enough to put the
        /// badge back on a chat the user just read. This is the local half only - the account is
        /// told by <see cref="MarkReadAsync"/>.
        /// </summary>
        Task ClearUnreadForChatAsync(string jid);

        /// <summary>Unread across every conversation, for the tile and the taskbar badge.</summary>
        int GetTotalUnreadCount();

        /// <summary>
        /// Writes just these list rows to the preview store. For a caller that already changed the
        /// strip on screen and only needs the mirror to agree; it does not rewrite names or aliases.
        /// </summary>
        void PersistChatListRows(IList<ChatItem> chats);

        /// <summary>
        /// Realigns the list strip with the newest row in SQLite where the list fell behind - a
        /// send from another device, or a history chunk that stored messages without touching the
        /// preview. Null checks every open row.
        /// </summary>
        Task ReconcileChatPreviewsFromSqliteAsync(
            IReadOnlyList<string> chatJids = null,
            string reason = null);

        /// <summary>
        /// Live inbound: create/find the list row, write the strip tip, bump unread when
        /// <see cref="LiveIncomingChatListApplyRequest.CountsAsUnread"/>, reposition for display.
        /// Must run list mutations on the UI thread. Returns the row and total unread for toast.
        /// </summary>
        Task<LiveIncomingChatListApplyResult> ApplyLiveIncomingChatListAsync(
            LiveIncomingChatListApplyRequest request);
    }
}
