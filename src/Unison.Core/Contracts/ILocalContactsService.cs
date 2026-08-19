using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Device People / address book. Read overlay plus the system contact card (WinRT in UWP).
    /// </summary>
    public interface ILocalContactsService
    {
        Task<Dictionary<string, string>> LoadPhoneContactNamesAsync();

        /// <summary>True when <paramref name="phoneDigits"/> matches a full number in the user agenda (not the Unison People list).</summary>
        bool IsPhoneInAddressBook(string phoneDigits);

        /// <summary>
        /// Opens the system People contact card (name, phone, optional photo).
        /// Does not write Unison's app contact list.
        /// </summary>
        Task<bool> ShowSystemContactCardAsync(string displayName, string phoneDigits, string photoUri);

        /// <summary>
        /// Writes these people into Unison's People account (<c>UserDataAccount</c> + list).
        /// Upserts; does not merge into the user address book.
        /// </summary>
        Task PublishAppContactsAsync(IReadOnlyList<AppContactExport> contacts);

        /// <summary>Deletes the Unison <c>UserDataAccount</c> so the names disappear from People.</summary>
        Task ClearPublishedAppContactsAsync();
    }
}
