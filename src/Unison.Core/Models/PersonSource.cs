namespace Unison.Core.Models
{
    /// <summary>
    /// How Unison learned this person. Stored as INTEGER in SQLite.
    /// Higher values win; never downgrade.
    /// </summary>
    public enum PersonSource
    {
        /// <summary>Row exists but origin is unknown (legacy DB).</summary>
        Unknown = 0,

        /// <summary>Push name, history, or group participant.</summary>
        Observed = 1,

        /// <summary>Has a 1:1 chat on this account.</summary>
        DirectChat = 2,

        /// <summary>Matched a name saved in the device address book.</summary>
        AddressBook = 3
    }
}
