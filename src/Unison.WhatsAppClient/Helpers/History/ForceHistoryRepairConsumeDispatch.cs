// =============================================================================
// ForceHistoryRepairConsumeDispatch
//
// Body of TryConsumeMessageStoreForceHistoryRepairAsync. Settings / request I/O
// stay on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class ForceHistoryRepairConsumeDispatch
    {
        public static async Task RunAsync(IForceHistoryRepairConsumeHost host, string reason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            bool pending;
            try
            {
                pending = host.TryReadForceHistoryRepairPending();
            }
            catch
            {
                return;
            }

            if (!pending)
            {
                return;
            }

            host.LogForceHistoryRepair(
                "MessageStoreForceHistoryRepair pending — requesting full history (" +
                reason + ")");

            bool ok = await host.RequestFullHistoryOnDemandTrackedAsync(
                    "message-store-epoch:" + reason,
                    isFreshnessRepair: true)
                .ConfigureAwait(false);

            if (!ok)
            {
                host.LogForceHistoryRepair(
                    "MessageStoreForceHistoryRepair not consumed yet (" + reason +
                    "); will retry later.");
                return;
            }

            try
            {
                host.ClearForceHistoryRepairPending();
                host.LogForceHistoryRepair(
                    "MessageStoreForceHistoryRepair cleared after request (" + reason + ")");
            }
            catch (Exception ex)
            {
                host.LogForceHistoryRepair(
                    "Failed to clear MessageStoreForceHistoryRepair: " + ex.Message);
            }
        }
    }
}
