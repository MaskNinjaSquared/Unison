using System.Threading.Tasks;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Post-auth UI snapshot load and the deferred enrichment pass that must not block
    /// first paint.
    /// </summary>
    /// <remarks>
    /// The shell reaches these through <see cref="IConnectionService"/>. The client still
    /// owns the chats.json read and the background resolution timer
    /// (<c>ShutdownAsync</c> cancels it).
    /// </remarks>
    public interface IStartupUiHost
    {
        /// <summary>Loads chats.json / UI snapshot after auth is known.</summary>
        Task LoadPersistedUiStateAsync();

        /// <summary>Post-connect maintenance that must not block first paint.</summary>
        void StartDeferredStartupMaintenance();
    }
}
