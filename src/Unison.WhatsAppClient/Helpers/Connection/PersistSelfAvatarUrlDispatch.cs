// =============================================================================
// PersistSelfAvatarUrlDispatch
//
// Body of PersistSelfAvatarUrl + PersistSelfAvatarUrlAsync. Auth store I/O
// stays on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class PersistSelfAvatarUrlDispatch
    {
        public static void Run(IPersistSelfAvatarUrlHost host, string avatarUrl)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (!host.HasSelfMe)
            {
                return;
            }

            if (string.Equals(host.SelfMeAvatarUrl, avatarUrl, StringComparison.Ordinal))
            {
                return;
            }

            host.SetSelfMeAvatarUrl(avatarUrl);
            if (!host.CanPersistAuth)
            {
                return;
            }

            _ = RunSaveAsync(host);
        }

        public static async Task RunSaveAsync(IPersistSelfAvatarUrlHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (!host.CanPersistAuth)
            {
                return;
            }

            try
            {
                await host.SaveAuthStateAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                host.LogSelfAvatarPersistFailed(ex.Message);
            }
        }
    }
}
