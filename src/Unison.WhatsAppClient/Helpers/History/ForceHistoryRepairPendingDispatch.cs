// =============================================================================
// ForceHistoryRepairPendingDispatch
//
// Body of TryMarkForceHistoryRepairPending. Settings I/O stays on the host.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class ForceHistoryRepairPendingDispatch
    {
        public static void Run(IForceHistoryRepairPendingHost host, string reason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            try
            {
                host.SetForceHistoryRepairPending();
                host.LogForceHistoryRepairPending(
                    "MessageStoreForceHistoryRepair set (" + (reason ?? string.Empty) + ")");
                host.WriteForceHistoryRepairLatched(reason);
            }
            catch (Exception ex)
            {
                host.LogForceHistoryRepairPending(
                    "Failed to set MessageStoreForceHistoryRepair (" +
                    (reason ?? string.Empty) + "): " + ex.Message);
            }
        }
    }
}
