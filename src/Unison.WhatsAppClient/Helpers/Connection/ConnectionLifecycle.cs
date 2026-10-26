// =============================================================================
// ConnectionLifecycle
//
// Suspend (incoming journal flush) and shutdown (latch, stop monitors, disconnect,
// optional best-effort catalogue persist).
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    /// <summary>Orchestrates suspend/shutdown against <see cref="IConnectionLifecycleHost"/>.</summary>
    public static class ConnectionLifecycle
    {
        public static readonly int IncomingDrainTimeoutMs = 250;

        public static readonly int SuspendPersistBudgetMs = 1600;

        public static async Task PrepareForSuspendAsync(IConnectionLifecycleHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            try
            {
                await host.FlushIncomingJournalForSuspendAsync().ConfigureAwait(false);
                host.WriteLifecycleJournal("suspend-incoming-journal-flushed");
            }
            catch (Exception ex)
            {
                host.RecordLifecycleException("suspend-incoming-journal-failed", ex);
            }
        }

        public static async Task ShutdownAsync(IConnectionLifecycleHost host, bool persist)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            host.BeginShutdownLatch();
            host.StopHealthMonitor("shutdown");
            host.CancelBackgroundWorkForShutdown();
            host.ResetPersistScheduler();

            // Mandatory: journal twice around the pump drain (same order as before).
            await PrepareForSuspendAsync(host).ConfigureAwait(false);
            await host.WaitForIncomingQueueDrainAsync(IncomingDrainTimeoutMs).ConfigureAwait(false);
            await PrepareForSuspendAsync(host).ConfigureAwait(false);

            try
            {
                host.DisconnectAndDisposeSocket();
                host.PublishSuspended();
                host.ResetIncomingPumpForSuspend();
            }
            catch (Exception ex)
            {
                host.RecordLifecycleException("shutdown-disconnect-failed", ex);
            }

            if (!persist)
            {
                return;
            }

            try
            {
                await host.PersistSuspendTailWithBudgetAsync(SuspendPersistBudgetMs).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                host.RecordLifecycleException("shutdown-persist-failed", ex);
            }
        }
    }
}
