// =============================================================================
// PostSyncMaintenanceOwner
//
// Owns post-replay + post-message enrichment schedule state: CTS fields, the
// once-per-session enrichment CAS, and Task.Run arms. WhatsAppService only
// supplies quiet/memory/façade work via IPostSyncMaintenanceIoHost (same shape
// as SoftReconnectCoordinator / ConnectionHealthMonitorOwner).
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;

namespace Unison.WhatsAppClient.Connection
{
    public sealed class PostSyncMaintenanceOwner :
        ISchedulePostReplayMaintenanceHost,
        IPostMessageEnrichmentScheduleHost
    {
        private readonly IPostSyncMaintenanceIoHost _io;
        private CancellationTokenSource _postReplayCts;
        private CancellationTokenSource _enrichmentCts;
        private int _enrichmentStarted;

        public PostSyncMaintenanceOwner(IPostSyncMaintenanceIoHost io)
        {
            _io = io ?? throw new ArgumentNullException(nameof(io));
        }

        /// <summary>True after this session claimed post-message enrichment.</summary>
        public bool HasStartedEnrichment =>
            Volatile.Read(ref _enrichmentStarted) != 0;

        /// <summary>True while a post-replay maintenance CTS is armed.</summary>
        public bool IsPostReplayArmed => Volatile.Read(ref _postReplayCts) != null;

        /// <summary>True while a post-message enrichment CTS is armed.</summary>
        public bool IsEnrichmentArmed => Volatile.Read(ref _enrichmentCts) != null;

        public void SchedulePostReplay(int offlineCount) =>
            SchedulePostReplayMaintenanceDispatch.Run(this, offlineCount);

        public void ScheduleEnrichment(string reason) =>
            PostMessageEnrichmentScheduleDispatch.Schedule(this, reason);

        /// <summary>
        /// Cancels in-flight post-replay / enrichment (shutdown / suspend hygiene).
        /// </summary>
        public void CancelAll()
        {
            CancelAndClear(ref _postReplayCts);
            CancelAndClear(ref _enrichmentCts);
        }

        /// <summary>
        /// Clears the once-per-session enrichment claim so a new connect may schedule again.
        /// </summary>
        public void ResetEnrichmentStarted() =>
            Interlocked.Exchange(ref _enrichmentStarted, 0);

        // --- ISchedulePostReplayMaintenanceHost ------------------------------

        void ISchedulePostReplayMaintenanceHost.CancelPreviousPostReplayMaintenanceCts()
        {
            CancellationTokenSource previous = Volatile.Read(ref _postReplayCts);
            if (previous != null)
            {
                try
                {
                    previous.Cancel();
                }
                catch
                {
                }
            }
        }

        CancellationTokenSource ISchedulePostReplayMaintenanceHost.ReplacePostReplayMaintenanceCts()
        {
            var cts = new CancellationTokenSource();
            Interlocked.Exchange(ref _postReplayCts, cts);
            return cts;
        }

        void ISchedulePostReplayMaintenanceHost.FireAndForgetPostReplayMaintenance(
            Func<Task> work) =>
            _ = Task.Run(work);

        Task ISchedulePostReplayMaintenanceHost.RunPostReplayMaintenanceAsync(
            int offlineCount,
            CancellationToken token) =>
            _io.RunPostReplayMaintenanceAsync(offlineCount, token);

        void ISchedulePostReplayMaintenanceHost.ClearPostReplayMaintenanceCtsIfCurrent(
            CancellationTokenSource cts)
        {
            Interlocked.CompareExchange(ref _postReplayCts, null, cts);
        }

        // --- IPostMessageEnrichmentScheduleHost ------------------------------

        bool IPostMessageEnrichmentScheduleHost.TryClaimPostMessageEnrichmentStarted() =>
            Interlocked.Exchange(ref _enrichmentStarted, 1) == 0;

        void IPostMessageEnrichmentScheduleHost.ClearEnrichmentAwaitingHeavyHistory() =>
            _io.ClearEnrichmentAwaitingHeavyHistory();

        CancellationTokenSource IPostMessageEnrichmentScheduleHost.ReplacePostMessageEnrichmentCts()
        {
            CancellationTokenSource previous = Interlocked.Exchange(
                ref _enrichmentCts,
                null);
            if (previous != null)
            {
                try
                {
                    previous.Cancel();
                }
                catch
                {
                }

                try
                {
                    previous.Dispose();
                }
                catch
                {
                }
            }

            var cts = new CancellationTokenSource();
            Interlocked.Exchange(ref _enrichmentCts, cts);
            return cts;
        }

        void IPostMessageEnrichmentScheduleHost.LogPostMessageEnrichment(string message) =>
            _io.LogPostMessageEnrichment(message);

        void IPostMessageEnrichmentScheduleHost.WritePostMessageEnrichmentScheduled(
            string reason) =>
            _io.WritePostMessageEnrichmentScheduled(reason);

        void IPostMessageEnrichmentScheduleHost.FireAndForgetPostMessageEnrichment(
            Func<Task> work) =>
            _ = Task.Run(work);

        Task IPostMessageEnrichmentScheduleHost.RunPostMessageEnrichmentAsync(
            string reason,
            CancellationToken token) =>
            _io.RunPostMessageEnrichmentAsync(reason, token);

        void IPostMessageEnrichmentScheduleHost.ClearPostMessageEnrichmentCtsIfCurrent(
            CancellationTokenSource cts)
        {
            Interlocked.CompareExchange(ref _enrichmentCts, null, cts);
        }

        private static void CancelAndClear(ref CancellationTokenSource field)
        {
            CancellationTokenSource cts = Interlocked.Exchange(ref field, null);
            if (cts == null)
            {
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch
            {
            }

            try
            {
                cts.Dispose();
            }
            catch
            {
            }
        }
    }
}
