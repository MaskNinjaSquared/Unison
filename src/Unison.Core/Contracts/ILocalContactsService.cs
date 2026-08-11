using System.Collections.Generic;
using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Reads device address-book display names keyed by normalized phone digits.
    /// </summary>
    public interface ILocalContactsService
    {
        Task<Dictionary<string, string>> LoadPhoneContactNamesAsync();
    }
}
