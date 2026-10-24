// =============================================================================
// EndHistoryCatchUpBannerDispatch
//
// Body of EndHistoryCatchUpBanner. Contacts apply stays on the host.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class EndHistoryCatchUpBannerDispatch
    {
        public static void Run(IEndHistoryCatchUpBannerHost host, string reason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (!host.TryEndHistoryCatchUpBanner())
            {
                return;
            }

            host.WriteCatchUpBannerEnd(reason);
            // The post-sync enrichment that would rename the list gives up when the window is
            // hidden or the socket dropped; names learned from the sync itself need neither.
            host.FireAndForgetApplyResolvedNamesToChatList();
        }
    }
}
