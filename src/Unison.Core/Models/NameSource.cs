namespace Unison.Core.Models
{
    /// <summary>
    /// Where a display name came from, ordered by how much it is trusted. Higher values win.
    /// Not persisted; stored rows keep <see cref="PersonSource"/> and convert on read.
    /// </summary>
    public enum NameSource
    {
        /// <summary>No name known; the address's number stands in.</summary>
        Number = 0,

        /// <summary>The push name, chosen by the contact themselves.</summary>
        PushName = 1,

        /// <summary>Learned and stored by this app (history, participant labels, chats).</summary>
        Learned = 2,

        /// <summary>The user's own words for this contact, from the device address book.</summary>
        AddressBook = 3
    }
}
