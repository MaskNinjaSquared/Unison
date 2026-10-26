// =============================================================================
// BeginHistoryCatchUpBannerDispatch
//
// Body of BeginHistoryCatchUpBanner. Sync channel / journal stay on the host.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class BeginHistoryCatchUpBannerDispatch
    {
        public static void Run(IBeginHistoryCatchUpBannerHost host, string reason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            host.LatchHistoryCatchUpBanner();
            host.NoteFullHistoryCatchUpProgress("banner-begin:" + (reason ?? string.Empty));
            host.WriteCatchUpBannerBegin(reason);
        }
    }
}
