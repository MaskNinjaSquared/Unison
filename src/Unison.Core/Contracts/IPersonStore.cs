using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// SQLite-backed contact / participant store (not the logged-in Profile).
    /// </summary>
    public interface IPersonStore
    {
        Task InitializeAsync();

        Task<Person> GetAsync(string jid);

        /// <summary>
        /// In-memory only (no disk I/O). Null when the JID has not been loaded yet.
        /// </summary>
        Person TryGetCached(string jid);

        /// <summary>
        /// Inserts or updates when <see cref="Person.RequiresUpdate"/> is true.
        /// Returns true when a write occurred.
        /// </summary>
        Task<bool> UpsertIfChangedAsync(string jid, string name, string avatarUrl, string phone);
    }
}
