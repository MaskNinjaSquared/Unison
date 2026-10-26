// =============================================================================
// ConnectionBrokerTransfer
//
// Warm handoff of a ready foreground socket to the out-of-process broker.
// Answers true only when ownership moved — never fakes a transfer.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    /// <summary>Orchestrates broker transfer against <see cref="IConnectionBrokerTransferHost"/>.</summary>
    public static class ConnectionBrokerTransfer
    {
        public static async Task<bool> RunAsync(IConnectionBrokerTransferHost host, string reason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (!host.HasReadyForegroundSocket)
            {
                host.WriteBrokerJournal(
                    "transfer-skipped",
                    "reason=no-ready-socket; trigger=" + (reason ?? string.Empty));
                return false;
            }

            if (host.IsAlreadyBrokerOwned)
            {
                return true;
            }

            host.StopHealthMonitor("socket-broker-transfer");
            try
            {
                Task displayNameSnapshotTask = host.PersistBackgroundDisplayNamesAsync();
                await host.PrepareForSuspendAsync().ConfigureAwait(false);
                await displayNameSnapshotTask.ConfigureAwait(false);

                bool transferred = await host.TransferSocketToBrokerAsync(reason).ConfigureAwait(false);
                if (transferred)
                {
                    host.PublishBackgroundConnection();
                    host.WriteBrokerJournal(
                        "service-transfer-complete",
                        "reason=" + (reason ?? string.Empty));
                    return true;
                }
            }
            catch (Exception ex)
            {
                host.RecordBrokerException("service-transfer-failed", ex);
            }

            host.RestartHealthMonitor();
            return false;
        }
    }
}
