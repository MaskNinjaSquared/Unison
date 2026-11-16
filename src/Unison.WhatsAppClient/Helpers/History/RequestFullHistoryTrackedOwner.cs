// =============================================================================
// RequestFullHistoryTrackedOwner
//
// Owns IRequestFullHistoryOnDemandTrackedHost: sends FULL_HISTORY through the
// on-demand dispatch, logs/journals the request and raises the catch-up banner.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class RequestFullHistoryTrackedOwner : IRequestFullHistoryOnDemandTrackedHost
    {
        private readonly Func<TimeSpan> _idleAbort;
        private readonly TimeSpan _responseTimeout;
        private readonly Func<string, bool, DateTime, Task<HistoryFullRequestResult>> _requestFullAsync;
        private readonly Action<string> _beginCatchUpBanner;
        private readonly Action _notifyBackgroundSyncBounced;
        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly Action<string> _log;

        public RequestFullHistoryTrackedOwner(
            Func<TimeSpan> idleAbort,
            TimeSpan responseTimeout,
            Func<string, bool, DateTime, Task<HistoryFullRequestResult>> requestFullAsync,
            Action<string> beginCatchUpBanner,
            Action notifyBackgroundSyncBounced,
            IRuntimeDiagnostics diagnostics,
            Action<string> log)
        {
            _idleAbort = idleAbort ?? throw new ArgumentNullException(nameof(idleAbort));
            _responseTimeout = responseTimeout;
            _requestFullAsync = requestFullAsync ?? throw new ArgumentNullException(nameof(requestFullAsync));
            _beginCatchUpBanner = beginCatchUpBanner
                ?? throw new ArgumentNullException(nameof(beginCatchUpBanner));
            _notifyBackgroundSyncBounced = notifyBackgroundSyncBounced
                ?? throw new ArgumentNullException(nameof(notifyBackgroundSyncBounced));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public Task<bool> RunAsync(string reason, bool isFreshnessRepair) =>
            RequestFullHistoryOnDemandTrackedDispatch.RunAsync(this, reason, isFreshnessRepair);

        TimeSpan IRequestFullHistoryOnDemandTrackedHost.FullHistoryCatchUpIdleAbort => _idleAbort();

        TimeSpan IRequestFullHistoryOnDemandTrackedHost.FullHistoryOnDemandResponseTimeout => _responseTimeout;

        Task<HistoryFullRequestResult> IRequestFullHistoryOnDemandTrackedHost.RequestFullHistoryAsync(
            string reason,
            bool isFreshnessRepair,
            DateTime nowUtc) =>
            _requestFullAsync(reason, isFreshnessRepair, nowUtc);

        void IRequestFullHistoryOnDemandTrackedHost.LogFullHistoryRequested(
            string reason,
            string stanzaId,
            int baselineCount,
            bool isFreshnessRepair) =>
            _log(
                "Requested FULL_HISTORY_SYNC_ON_DEMAND (reason=" + reason +
                ", stanzaId=" + stanzaId +
                ", baseline=" + baselineCount +
                ", freshnessRepair=" + isFreshnessRepair +
                ", idleAbortMs=" + (int)_idleAbort().TotalMilliseconds +
                ", hardTimeoutMs=" + (int)_responseTimeout.TotalMilliseconds + ")");

        void IRequestFullHistoryOnDemandTrackedHost.WriteFullHistoryRequestedJournal(
            string reason,
            string stanzaId,
            int baselineCount,
            bool isFreshnessRepair) =>
            _diagnostics.Write(
                "history",
                "full-history-requested",
                "reason=" + (reason ?? string.Empty) +
                "; requestId=" + stanzaId +
                "; baselineMsgs=" + baselineCount +
                "; freshnessRepair=" + isFreshnessRepair +
                "; idleAbortMs=" + (int)_idleAbort().TotalMilliseconds +
                "; hardTimeoutMs=" + (int)_responseTimeout.TotalMilliseconds);

        void IRequestFullHistoryOnDemandTrackedHost.BeginHistoryCatchUpBanner(string reason) =>
            _beginCatchUpBanner(reason);

        void IRequestFullHistoryOnDemandTrackedHost.NotifyBackgroundHistorySyncBounced() =>
            _notifyBackgroundSyncBounced();
    }
}
