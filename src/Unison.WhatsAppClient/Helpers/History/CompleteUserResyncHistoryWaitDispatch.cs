// =============================================================================
// CompleteUserResyncHistoryWaitDispatch
//
// Body of CompleteUserResyncHistoryWait. TCS / settings I/O stay on the host.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class CompleteUserResyncHistoryWaitDispatch
    {
        public static void Run(ICompleteUserResyncHistoryWaitHost host, string reason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (!host.TryClaimUserResyncHistoryWaiter(out object waiter))
            {
                return;
            }

            host.ClearForceHistoryRepairPendingBestEffort();
            host.LogUserResyncHistoryWaitCompleted(reason);
            host.CompleteUserResyncHistoryWaiter(waiter);
        }
    }
}
