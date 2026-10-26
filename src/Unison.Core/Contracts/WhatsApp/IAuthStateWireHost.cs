// =============================================================================
// IAuthStateWireHost
//
// Side effects for AuthStateWireDispatch (OnAuthStateUpdate).
// =============================================================================
using System.Threading.Tasks;

namespace Unison.Core.Contracts.WhatsApp
{
    public interface IAuthStateWireHost
    {
        void LogAuthStateUpdated();

        void FileSelfIdentityAliases(string source);

        Task PersistAuthStateFromWireAsync();
    }
}
