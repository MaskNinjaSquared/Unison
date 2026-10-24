// =============================================================================
// ReleaseMemoryDispatch
//
// Body of ReleaseMemoryAsync. UI hop / cache dict stay on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class ReleaseMemoryDispatch
    {
        public static async Task RunAsync(IReleaseMemoryHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            try
            {
                host.ClearMessageStoreMemoryCache();

                await host.RunOnUiAsync(() =>
                {
                    var active = host.ActiveChatJid;
                    var keys = host.MessageCacheChatJids();
                    foreach (var key in keys)
                    {
                        if (!string.IsNullOrWhiteSpace(active) &&
                            string.Equals(
                                host.GetCanonicalJid(key),
                                active,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        host.RemoveMessagesByChatKey(key);
                    }
                }).ConfigureAwait(false);

                host.LogMemoryReleased();
            }
            catch (Exception ex)
            {
                host.LogMemoryReleaseFailed(ex);
            }
        }
    }
}
