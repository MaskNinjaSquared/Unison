// =============================================================================
// ResetFirstSyncListHoldFlagsDispatch
//
// Body of ResetFirstSyncListHoldFlags. Field clears stay on the host.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class ResetFirstSyncListHoldFlagsDispatch
    {
        public static void Run(IResetFirstSyncListHoldFlagsHost host, string reason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            host.ClearFirstSyncListHoldFlags();
            host.LogFirstSyncListHoldFlagsReset(reason);
        }
    }
}
