namespace Unison.Core.Models
{
    /// <summary>
    /// Which slice of the chat list the user asked to see. Bound from the filter flyout as an
    /// integer <c>CommandParameter</c> and cast into this enum in the view model.
    /// </summary>
    public enum ChatListFilter
    {
        /// <summary>No filter — every conversation that would otherwise be shown.</summary>
        All = 0,

        /// <summary>Rows with <see cref="ChatItem.HasUnread"/>.</summary>
        Unread = 1,

        /// <summary>Rows marked favourite. Not implemented yet; currently always empty.</summary>
        Favorites = 2,

        /// <summary>1:1 chats whose JID is in the device address book.</summary>
        Contacts = 3,

        /// <summary>1:1 chats that are not in the device address book.</summary>
        NonContacts = 4,

        /// <summary>Group conversations.</summary>
        Groups = 5,

        /// <summary>Chats with a local draft. Not implemented yet; currently always empty.</summary>
        Drafts = 6
    }
}
