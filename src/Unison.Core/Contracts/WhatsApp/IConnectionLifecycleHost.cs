using System.Threading.Tasks;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Suspend / shutdown primitives <c>ConnectionLifecycle</c> needs from the connection client.
    /// </summary>
    public interface IConnectionLifecycleHost
    {
        Task FlushIncomingJournalForSuspendAsync();

        void WriteLifecycleJournal(string eventName, string detail = null);

        void RecordLifecycleException(string eventName, System.Exception error);

        /// <summary>Latches force-fresh on next resume and suppresses soft reconnect.</summary>
        void BeginShutdownLatch();

        void StopHealthMonitor(string reason);

        void CancelBackgroundWorkForShutdown();

        void ResetPersistScheduler();

        Task WaitForIncomingQueueDrainAsync(int timeoutMs);

        void DisconnectAndDisposeSocket();

        void PublishSuspended();

        void ResetIncomingPumpForSuspend();

        /// <summary>
        /// Best-effort catalogue/alias snapshot with a short budget. Must not wait on a
        /// large per-chat rewrite — the incoming journal already covers recent messages.
        /// </summary>
        Task PersistSuspendTailWithBudgetAsync(int budgetMs);
    }
}
