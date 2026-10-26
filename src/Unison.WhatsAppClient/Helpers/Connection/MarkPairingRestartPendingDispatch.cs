// =============================================================================
// MarkPairingRestartPendingDispatch
//
// Body of MarkPairingRestartPending. Pairing flags stay on the host.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class MarkPairingRestartPendingDispatch
    {
        public static void Run(IMarkPairingRestartPendingHost host, string reason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            bool wasPending = host.PairingRestartPending;
            host.PairingRestartPending = true;
            host.ClearPreSessionCloseAsFatal();
            host.ResetPreSessionCloseStreak();
            if (!wasPending)
            {
                host.TracePairingRestartPending(reason);
            }
        }
    }
}
