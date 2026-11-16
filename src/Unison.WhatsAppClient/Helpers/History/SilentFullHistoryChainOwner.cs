// =============================================================================
// SilentFullHistoryChainOwner
//
// Owns ISilentFullHistoryChainHost and the chain's state (armed, previous lot
// quiet, consecutive empty lots, schedule CTS). Socket readiness, the tracked
// FULL request and the banner stay ports.
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class SilentFullHistoryChainOwner : ISilentFullHistoryChainHost
    {
        private readonly HistoryOnDemandRegistry _historyRequests;
        private readonly TimeSpan _silentLotDelay;
        private readonly Func<bool> _isSocketReady;
        private readonly Func<bool> _isHistoryCatchUpActive;
        private readonly TryGetHistoryStaleReason _tryGetStaleReason;
        private readonly Func<string, Task<bool>> _requestSilentFullHistoryAsync;
        private readonly Action<string> _beginCatchUpBanner;
        private readonly Action<string> _endCatchUpBanner;
        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly Action<string> _log;
        private bool _armed;
        private bool _previousLotQuiet = true;
        private int _consecutiveEmptyLots;
        private CancellationTokenSource _scheduleCts;

        public SilentFullHistoryChainOwner(
            HistoryOnDemandRegistry historyRequests,
            TimeSpan silentLotDelay,
            Func<bool> isSocketReady,
            Func<bool> isHistoryCatchUpActive,
            TryGetHistoryStaleReason tryGetStaleReason,
            Func<string, Task<bool>> requestSilentFullHistoryAsync,
            Action<string> beginCatchUpBanner,
            Action<string> endCatchUpBanner,
            IRuntimeDiagnostics diagnostics,
            Action<string> log)
        {
            _historyRequests = historyRequests
                ?? throw new ArgumentNullException(nameof(historyRequests));
            _silentLotDelay = silentLotDelay;
            _isSocketReady = isSocketReady ?? throw new ArgumentNullException(nameof(isSocketReady));
            _isHistoryCatchUpActive = isHistoryCatchUpActive
                ?? throw new ArgumentNullException(nameof(isHistoryCatchUpActive));
            _tryGetStaleReason = tryGetStaleReason
                ?? throw new ArgumentNullException(nameof(tryGetStaleReason));
            _requestSilentFullHistoryAsync = requestSilentFullHistoryAsync
                ?? throw new ArgumentNullException(nameof(requestSilentFullHistoryAsync));
            _beginCatchUpBanner = beginCatchUpBanner
                ?? throw new ArgumentNullException(nameof(beginCatchUpBanner));
            _endCatchUpBanner = endCatchUpBanner ?? throw new ArgumentNullException(nameof(endCatchUpBanner));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public void Arm(string reason, bool fireSoon = true) =>
            SilentFullHistoryChainDispatch.Arm(this, reason, fireSoon);

        public void Stop(string reason) => SilentFullHistoryChainDispatch.Stop(this, reason);

        public void OnLotQuiet(string triggerReason, int appliedCount) =>
            SilentFullHistoryChainDispatch.OnLotQuiet(this, triggerReason, appliedCount);

        public void OnLoudCatchUpFinished(string finishReason) =>
            SilentFullHistoryChainDispatch.OnLoudCatchUpFinished(this, finishReason);

        bool ISilentFullHistoryChainHost.IsSilentChainArmed => _armed;

        bool ISilentFullHistoryChainHost.IsPreviousSilentLotQuiet => _previousLotQuiet;

        int ISilentFullHistoryChainHost.ConsecutiveEmptySilentLots => _consecutiveEmptyLots;

        bool ISilentFullHistoryChainHost.IsSocketReadyForSilentFull => _isSocketReady();

        bool ISilentFullHistoryChainHost.IsHistoryCatchUpActive => _isHistoryCatchUpActive();

        bool ISilentFullHistoryChainHost.IsFullHistoryClaimed => _historyRequests.IsFullClaimed;

        bool ISilentFullHistoryChainHost.IsHistoryTipStale => _tryGetStaleReason(DateTime.UtcNow, out _);

        TimeSpan ISilentFullHistoryChainHost.SilentLotDelay => _silentLotDelay;

        void ISilentFullHistoryChainHost.MarkSilentChainArmed(bool armed) => _armed = armed;

        void ISilentFullHistoryChainHost.MarkPreviousSilentLotQuiet(bool quiet) => _previousLotQuiet = quiet;

        void ISilentFullHistoryChainHost.MarkConsecutiveEmptySilentLots(int count) =>
            _consecutiveEmptyLots = count < 0 ? 0 : count;

        void ISilentFullHistoryChainHost.CancelSilentSchedule()
        {
            try
            {
                _scheduleCts?.Cancel();
            }
            catch
            {
            }

            _scheduleCts?.Dispose();
            _scheduleCts = null;
        }

        CancellationTokenSource ISilentFullHistoryChainHost.CreateSilentScheduleCts()
        {
            var cts = new CancellationTokenSource();
            _scheduleCts = cts;
            return cts;
        }

        void ISilentFullHistoryChainHost.StartSilentScheduleTask(Func<Task> work)
        {
            if (work == null)
            {
                return;
            }

            _ = Task.Run(work);
        }

        Task ISilentFullHistoryChainHost.DelayAsync(TimeSpan delay, CancellationToken token) =>
            Task.Delay(delay, token);

        Task<bool> ISilentFullHistoryChainHost.RequestSilentFullHistoryAsync(string reason) =>
            _requestSilentFullHistoryAsync(reason ?? SilentFullHistoryPolicy.ReasonPrefix + "tick");

        void ISilentFullHistoryChainHost.BeginHistoryCatchUpBanner(string reason) =>
            _beginCatchUpBanner(reason);

        void ISilentFullHistoryChainHost.EndHistoryCatchUpBanner(string reason) =>
            _endCatchUpBanner(reason);

        void ISilentFullHistoryChainHost.WriteSilentJournal(string eventName, string detail) =>
            _diagnostics.Write(
                "history",
                "silent-sync",
                "event=" + (eventName ?? string.Empty) +
                "; " + (detail ?? string.Empty));

        void ISilentFullHistoryChainHost.LogSilentChainFailed(string message) =>
            _log("Silent FULL chain failed: " + message);
    }
}
