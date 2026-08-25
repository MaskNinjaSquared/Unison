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
    }
}
