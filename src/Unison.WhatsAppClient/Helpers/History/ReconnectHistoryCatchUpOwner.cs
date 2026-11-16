// =============================================================================
// ReconnectHistoryCatchUpOwner
//
// Owns IReconnectHistoryCatchUpHost: after reconnect, waits for startup quiet
// and asks a tracked FULL_HISTORY so the tips catch up.
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class ReconnectHistoryCatchUpOwner : IReconnectHistoryCatchUpHost
    {
        private readonly HistoryOnDemandRegistry _historyRequests;
        private readonly ReleaseEnrichmentAfterHeavyHistoryCoordinator _heavyHistoryEnrichment;
        private readonly Func<bool> _isHistoryCatchUpActive;
        private readonly Func<bool> _isConnected;
        private readonly Func<bool> _isWindowVisible;
        private readonly Func<TimeSpan, string, CancellationToken, Task<bool>> _waitForStartupQuietAsync;
        private readonly Func<string, bool, Task<bool>> _requestFullHistoryTrackedAsync;
        private readonly Action<string> _trySchedulePostMessageEnrichment;
        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly Action<string> _log;

        public ReconnectHistoryCatchUpOwner(
            HistoryOnDemandRegistry historyRequests,
            ReleaseEnrichmentAfterHeavyHistoryCoordinator heavyHistoryEnrichment,
            Func<bool> isHistoryCatchUpActive,
            Func<bool> isConnected,
            Func<bool> isWindowVisible,
            Func<TimeSpan, string, CancellationToken, Task<bool>> waitForStartupQuietAsync,
            Func<string, bool, Task<bool>> requestFullHistoryTrackedAsync,
            Action<string> trySchedulePostMessageEnrichment,
            IRuntimeDiagnostics diagnostics,
            Action<string> log)
        {
            _historyRequests = historyRequests
                ?? throw new ArgumentNullException(nameof(historyRequests));
            _heavyHistoryEnrichment = heavyHistoryEnrichment
                ?? throw new ArgumentNullException(nameof(heavyHistoryEnrichment));
            _isHistoryCatchUpActive = isHistoryCatchUpActive
                ?? throw new ArgumentNullException(nameof(isHistoryCatchUpActive));
            _isConnected = isConnected ?? throw new ArgumentNullException(nameof(isConnected));
            _isWindowVisible = isWindowVisible ?? throw new ArgumentNullException(nameof(isWindowVisible));
            _waitForStartupQuietAsync = waitForStartupQuietAsync
                ?? throw new ArgumentNullException(nameof(waitForStartupQuietAsync));
            _requestFullHistoryTrackedAsync = requestFullHistoryTrackedAsync
                ?? throw new ArgumentNullException(nameof(requestFullHistoryTrackedAsync));
            _trySchedulePostMessageEnrichment = trySchedulePostMessageEnrichment
                ?? throw new ArgumentNullException(nameof(trySchedulePostMessageEnrichment));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public Task RunAsync(string reason) => ReconnectHistoryCatchUpDispatch.RunAsync(this, reason);

        bool IReconnectHistoryCatchUpHost.IsHistoryCatchUpActive => _isHistoryCatchUpActive();

        bool IReconnectHistoryCatchUpHost.IsConnected => _isConnected();

        bool IReconnectHistoryCatchUpHost.IsWindowVisible => _isWindowVisible();

        Task<bool> IReconnectHistoryCatchUpHost.WaitForStartupQuietAsync(
            TimeSpan timeout,
            string reason,
            CancellationToken token) =>
            _waitForStartupQuietAsync(timeout, reason, token);

        void IReconnectHistoryCatchUpHost.ReleaseFullHistoryIfUntracked() =>
            _historyRequests.ReleaseFullIfUntracked();

        Task<bool> IReconnectHistoryCatchUpHost.RequestFullHistoryOnDemandTrackedAsync(
            string reason,
            bool isFreshnessRepair) =>
            _requestFullHistoryTrackedAsync(reason, isFreshnessRepair);

        void IReconnectHistoryCatchUpHost.ClearEnrichmentAwaitingHeavyHistory() =>
            _heavyHistoryEnrichment.SetAwaitingHeavyHistory(false);

        void IReconnectHistoryCatchUpHost.TrySchedulePostMessageEnrichment(string reason) =>
            _trySchedulePostMessageEnrichment(reason);

        void IReconnectHistoryCatchUpHost.LogReconnectHistoryCatchUp(string message) => _log(message);

        void IReconnectHistoryCatchUpHost.WriteReconnectHistoryCatchUpJournal(string reason) =>
            _diagnostics.Write(
                "startup",
                "reconnect-history-catchup-requested",
                "reason=" + reason);
    }
}
