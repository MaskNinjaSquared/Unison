// =============================================================================
// IAuthStateWireHost
//
// Side effects for AuthStateWireDispatch (OnAuthStateUpdate).
// =============================================================================
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IAuthStateWireHost
    {
        void LogAuthStateUpdated();

        void FileSelfIdentityAliases(string source);

        Task PersistAuthStateFromWireAsync();
    }
}
