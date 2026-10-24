// =============================================================================
// AuthStateWireDispatch
//
// OnAuthStateUpdate body: file self-identity aliases, persist. Handler wiring
// stays on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    public static class AuthStateWireDispatch
    {
        public static async Task RunAsync(IAuthStateWireHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            host.LogAuthStateUpdated();
            host.FileSelfIdentityAliases("auth-update-identity");
            await host.PersistAuthStateFromWireAsync().ConfigureAwait(false);
        }
    }
}
