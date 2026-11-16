// =============================================================================
// PeriodicFullHistorySafetyOwner
//
// Owns IPeriodicFullHistorySafetyLoopHost + IPeriodicFullHistorySafetyHost:
// the standby loop and its tick (re-solicit FULL when tips still look owed or the
// force-repair latch is set). Socket readiness and the tracked FULL send are ports.
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class PeriodicFullHistorySafetyOwner :
        IPeriodicFullHistorySafetyLoopHost,
        IPeriodicFullHistorySafetyHost
    {
        private readonly HistoryOnDemandRegistry _historyRequests;
        private readonly bool _allowStagnantRepairCascade;
        private readonly ILocalSettings _settings;
        private readonly Func<bool> _isSocketReady;
        private readonly Func<bool> _isHistoryCatchUpActive;
        private readonly TryGetHistoryStaleReason _tryGetStaleReason;
        private readonly Func<string, bool, Task<bool>> _requestFullHistoryTrackedAsync;
        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly Action<string> _log;

        public PeriodicFullHistorySafetyOwner(
            HistoryOnDemandRegistry historyRequests,
            bool allowStagnantRepairCascade,
            ILocalSettings settings,
            Func<bool> isSocketReady,
            Func<bool> isHistoryCatchUpActive,
            TryGetHistoryStaleReason tryGetStaleReason,
            Func<string, bool, Task<bool>> requestFullHistoryTrackedAsync,
            IRuntimeDiagnostics diagnostics,
            Action<string> log)
        {
            _historyRequests = historyRequests
                ?? throw new ArgumentNullException(nameof(historyRequests));
            _allowStagnantRepairCascade = allowStagnantRepairCascade;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _isSocketReady = isSocketReady ?? throw new ArgumentNullException(nameof(isSocketReady));
            _isHistoryCatchUpActive = isHistoryCatchUpActive
                ?? throw new ArgumentNullException(nameof(isHistoryCatchUpActive));
            _tryGetStaleReason = tryGetStaleReason
                ?? throw new ArgumentNullException(nameof(tryGetStaleReason));
            _requestFullHistoryTrackedAsync = requestFullHistoryTrackedAsync
                ?? throw new ArgumentNullException(nameof(requestFullHistoryTrackedAsync));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public Task RunLoopAsync(CancellationToken token) =>
            PeriodicFullHistorySafetyLoopDispatch.RunAsync(this, token);

        /// <summary>One standby tick: solicit FULL again only when tips still look owed.</summary>
        public Task RunTickAsync() => PeriodicFullHistorySafetyDispatch.RunAsync(this);

        TimeSpan IPeriodicFullHistorySafetyLoopHost.PeriodicFullHistorySafetyInterval =>
            HistoryPeriodicFullSafetyDecision.Interval;

        Task IPeriodicFullHistorySafetyLoopHost.DelayAsync(TimeSpan delay, CancellationToken token) =>
            Task.Delay(delay, token);

        Task IPeriodicFullHistorySafetyLoopHost.RunPeriodicFullHistorySafetyTickAsync() => RunTickAsync();

        void IPeriodicFullHistorySafetyLoopHost.LogPeriodicFullHistorySafetyTickFailed(string message) =>
            _log("Periodic FULL_HISTORY safety tick failed: " + message);

        bool IPeriodicFullHistorySafetyHost.IsSocketReadyForPeriodicFullSafety => _isSocketReady();

        bool IPeriodicFullHistorySafetyHost.IsHistoryCatchUpActive => _isHistoryCatchUpActive();

        bool IPeriodicFullHistorySafetyHost.IsFullHistoryClaimed => _historyRequests.IsFullClaimed;

        bool IPeriodicFullHistorySafetyHost.AllowStagnantHistoryRepairCascade =>
            _allowStagnantRepairCascade;

        bool IPeriodicFullHistorySafetyHost.TryReadForceHistoryRepairPending()
        {
            try
            {
                return _settings.Get<bool>(LocalSettingsConstants.MessageStoreForceHistoryRepair);
            }
            catch
            {
                return false;
            }
        }

        bool IPeriodicFullHistorySafetyHost.TryGetFreshnessStaleReason(
            DateTime nowUtc,
            out string staleDetail) =>
            _tryGetStaleReason(nowUtc, out staleDetail);

        void IPeriodicFullHistorySafetyHost.LogPeriodicFullSafety(string message) => _log(message);

        void IPeriodicFullHistorySafetyHost.WritePeriodicFullSafetyJournal(string reason) =>
            _diagnostics.Write("history", "periodic-full-safety", "reason=" + reason);

        Task<bool> IPeriodicFullHistorySafetyHost.RequestFullHistoryOnDemandTrackedAsync(
            string reason,
            bool isFreshnessRepair) =>
            _requestFullHistoryTrackedAsync(reason, isFreshnessRepair);
    }
}
