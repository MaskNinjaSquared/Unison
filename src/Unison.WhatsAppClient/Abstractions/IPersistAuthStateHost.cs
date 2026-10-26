// =============================================================================
// IPersistAuthStateHost
//
// Side effects for PersistAuthStateDispatch.
// =============================================================================
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IPersistAuthStateHost
    {
        /// <summary>Auth from source socket, else current socket, else cached; null if none.</summary>
        object ResolveAuthToSave(object sourceSocket);

        void LogSkipAuthStateSave(string reason);

        Task AssignAndSaveAuthAsync(object auth);
    }
}
