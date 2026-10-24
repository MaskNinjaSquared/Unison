// =============================================================================
// PersistAuthStateDispatch
//
// Body of PersistAuthStateAsync. Auth store I/O stays on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class PersistAuthStateDispatch
    {
        public static async Task RunAsync(
            IPersistAuthStateHost host,
            object sourceSocket,
            string reason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            object authToSave = host.ResolveAuthToSave(sourceSocket);
            if (authToSave == null)
            {
                host.LogSkipAuthStateSave(reason);
                return;
            }

            await host.AssignAndSaveAuthAsync(authToSave).ConfigureAwait(false);
        }
    }
}
