using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Logged-in user profile: local hydrate vs network sync.
    /// </summary>
    public interface IProfileService
    {
        /// <summary>
        /// The logged-in user's name or picture changed, from any source: the app state sync,
        /// a fresh avatar fetch, or pairing filling in an identity that was empty.
        /// </summary>
        event System.EventHandler ProfileChanged;

        /// <summary>
        /// Returns the current profile from auth / in-memory state (no network).
        /// </summary>
        Profile GetCurrentProfile();

        /// <summary>
        /// Refreshes the current profile from WhatsApp (avatar IQ + cache) and persists.
        /// </summary>
        Task SyncCurrentProfileAsync(CancellationToken cancellationToken = default(CancellationToken));
    }
}
