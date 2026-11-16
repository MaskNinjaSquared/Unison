// =============================================================================
// HistoryReconnectFallbackOwner
//
// Owns IFreshnessReconnectFallbackHost + ICatchUpContinueReconnectHost and the
// state both share: the one-at-a-time "reconnect for history" flag and the last
// catch-up-continue stamp. Socket teardown / auto-reconnect stay ports.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class HistoryReconnectFallbackOwner :
        IFreshnessReconnectFallbackHost,
        ICatchUpContinueReconnectHost
    {
        private readonly HistoryCatchUpContinuation _catchUp;
        private readonly SyncStatusChannel _syncStatus;
        private readonly HistoryFreshnessRepairPersistence _freshnessRepair;
        private readonly ReleaseEnrichmentAfterHeavyHistoryCoordinator _heavyHistoryEnrichment;
        private readonly Func<bool> _suppressReconnect;
        private readonly Func<bool> _isAuthRegistered;
        private readonly TryGetHistoryStaleReason _tryGetStaleReason;
        private readonly Action<string, bool> _finishCatchUpContinue;
        private readonly Action<string> _beginCatchUpBanner;
        private readonly Action<string> _startHydrationFallback;
        private readonly Action<string> _publishConnectionStatus;
        private readonly Action<string> _stopHealthMonitor;
        private readonly Action _tearDownSocket;
        private readonly Action<string> _scheduleAutoReconnect;
        private readonly Action<Exception> _raiseError;
        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly Action<string> _log;
        private volatile bool _inProgress;
        private DateTime _lastCatchUpContinueUtc = DateTime.MinValue;

        public HistoryReconnectFallbackOwner(
            HistoryCatchUpContinuation catchUp,
            SyncStatusChannel syncStatus,
            HistoryFreshnessRepairPersistence freshnessRepair,
            ReleaseEnrichmentAfterHeavyHistoryCoordinator heavyHistoryEnrichment,
            Func<bool> suppressReconnect,
            Func<bool> isAuthRegistered,
            TryGetHistoryStaleReason tryGetStaleReason,
            Action<string, bool> finishCatchUpContinue,
            Action<string> beginCatchUpBanner,
            Action<string> startHydrationFallback,
            Action<string> publishConnectionStatus,
            Action<string> stopHealthMonitor,
            Action tearDownSocket,
            Action<string> scheduleAutoReconnect,
            Action<Exception> raiseError,
            IRuntimeDiagnostics diagnostics,
            Action<string> log)
        {
            _catchUp = catchUp ?? throw new ArgumentNullException(nameof(catchUp));
            _syncStatus = syncStatus ?? throw new ArgumentNullException(nameof(syncStatus));
            _freshnessRepair = freshnessRepair ?? throw new ArgumentNullException(nameof(freshnessRepair));
            _heavyHistoryEnrichment = heavyHistoryEnrichment
                ?? throw new ArgumentNullException(nameof(heavyHistoryEnrichment));
            _suppressReconnect = suppressReconnect
                ?? throw new ArgumentNullException(nameof(suppressReconnect));
            _isAuthRegistered = isAuthRegistered ?? throw new ArgumentNullException(nameof(isAuthRegistered));
            _tryGetStaleReason = tryGetStaleReason
                ?? throw new ArgumentNullException(nameof(tryGetStaleReason));
            _finishCatchUpContinue = finishCatchUpContinue
                ?? throw new ArgumentNullException(nameof(finishCatchUpContinue));
            _beginCatchUpBanner = beginCatchUpBanner
                ?? throw new ArgumentNullException(nameof(beginCatchUpBanner));
            _startHydrationFallback = startHydrationFallback
                ?? throw new ArgumentNullException(nameof(startHydrationFallback));
            _publishConnectionStatus = publishConnectionStatus
                ?? throw new ArgumentNullException(nameof(publishConnectionStatus));
            _stopHealthMonitor = stopHealthMonitor ?? throw new ArgumentNullException(nameof(stopHealthMonitor));
            _tearDownSocket = tearDownSocket ?? throw new ArgumentNullException(nameof(tearDownSocket));
            _scheduleAutoReconnect = scheduleAutoReconnect
                ?? throw new ArgumentNullException(nameof(scheduleAutoReconnect));
            _raiseError = raiseError ?? throw new ArgumentNullException(nameof(raiseError));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public void ScheduleFreshnessFallback(string triggerReason) =>
            FreshnessReconnectFallbackDispatch.Schedule(this, triggerReason);

        public void ScheduleCatchUpContinue(string triggerReason) =>
            CatchUpContinueReconnectDispatch.Schedule(this, triggerReason);

        bool IFreshnessReconnectFallbackHost.FreshnessReconnectFallbackInProgress
        {
            get => _inProgress;
            set => _inProgress = value;
        }

        bool IFreshnessReconnectFallbackHost.SuppressReconnect => _suppressReconnect();

        bool IFreshnessReconnectFallbackHost.IsAuthRegistered => _isAuthRegistered();

        DateTime IFreshnessReconnectFallbackHost.LastFreshnessReconnectFallbackUtc =>
            _freshnessRepair.LastFreshnessReconnectFallbackUtc;

        TimeSpan IFreshnessReconnectFallbackHost.FreshnessReconnectFallbackCooldown =>
            FreshnessReconnectFallbackGates.DefaultCooldown;

        TimeSpan IFreshnessReconnectFallbackHost.FreshnessReconnectDelay =>
            FreshnessReconnectFallbackGates.ReconnectDelay;

        bool IFreshnessReconnectFallbackHost.TryGetFreshnessStaleReason(
            DateTime nowUtc,
            out string staleReason) =>
            _tryGetStaleReason(nowUtc, out staleReason);

        void IFreshnessReconnectFallbackHost.PersistFreshnessReconnectFallbackUtc(DateTime utc) =>
            _freshnessRepair.PersistFreshnessReconnectFallbackUtc(utc);

        void IFreshnessReconnectFallbackHost.LogFreshnessFallback(string message) => _log(message);

        void IFreshnessReconnectFallbackHost.StartHydrationFallbackPipeline(string reason) =>
            _startHydrationFallback(reason);

        void IFreshnessReconnectFallbackHost.PublishFreshnessReconnectStatus(string status) =>
            _publishConnectionStatus(status);

        void IFreshnessReconnectFallbackHost.StopConnectionHealthMonitor(string reason) =>
            _stopHealthMonitor(reason);

        void IFreshnessReconnectFallbackHost.TearDownSocketForReconnect() => _tearDownSocket();

        void IFreshnessReconnectFallbackHost.ScheduleAutoReconnect(string reason) =>
            _scheduleAutoReconnect(reason);

        void IFreshnessReconnectFallbackHost.RaiseFreshnessFallbackError(Exception error) =>
            _raiseError(error);

        void IFreshnessReconnectFallbackHost.FireAndForgetFreshnessReconnect(Func<Task> work) =>
            _ = Task.Run(work);

        bool ICatchUpContinueReconnectHost.FreshnessReconnectFallbackInProgress
        {
            get => _inProgress;
            set => _inProgress = value;
        }

        bool ICatchUpContinueReconnectHost.SuppressReconnect => _suppressReconnect();

        bool ICatchUpContinueReconnectHost.IsAuthRegistered => _isAuthRegistered();

        DateTime ICatchUpContinueReconnectHost.LastCatchUpContinueUtc
        {
            get => _lastCatchUpContinueUtc;
            set => _lastCatchUpContinueUtc = value;
        }

        TimeSpan ICatchUpContinueReconnectHost.CatchUpContinueReconnectCooldown =>
            CatchUpContinueReconnectGates.DefaultCooldown;

        TimeSpan ICatchUpContinueReconnectHost.CatchUpContinueReconnectDelay =>
            CatchUpContinueReconnectGates.DefaultDelay;

        int ICatchUpContinueReconnectHost.CatchUpRound => _catchUp.Snapshot().Round;

        bool ICatchUpContinueReconnectHost.IsHistoryCatchUpBannerActive => _syncStatus.IsHistoryCatchUpShown;

        bool ICatchUpContinueReconnectHost.IsHistoryCatchUpActive() =>
            _catchUp.IsActive(_syncStatus.IsHistoryCatchUpShown);

        bool ICatchUpContinueReconnectHost.TryGetFreshnessStaleReason(
            DateTime nowUtc,
            out string staleReason) =>
            _tryGetStaleReason(nowUtc, out staleReason);

        void ICatchUpContinueReconnectHost.FinishHistoryCatchUpContinue(
            string reason,
            bool releaseEnrichment) =>
            _finishCatchUpContinue(reason, releaseEnrichment);

        void ICatchUpContinueReconnectHost.MarkEnrichmentAwaitingHeavyHistory() =>
            _heavyHistoryEnrichment.SetAwaitingHeavyHistory(true);

        void ICatchUpContinueReconnectHost.BeginHistoryCatchUpBanner(string reason) =>
            _beginCatchUpBanner(reason);

        void ICatchUpContinueReconnectHost.PublishHistoryCatchUpStatus() =>
            _syncStatus.Publish(SyncPhaseStatus.Format(SyncPhaseStatus.HistoryCatchUp));

        void ICatchUpContinueReconnectHost.WriteCatchUpContinueJournal(
            string triggerReason,
            int delayMs) =>
            _diagnostics.Write(
                "history",
                "catch-up-continue-reconnect",
                "trigger=" + (triggerReason ?? string.Empty) +
                "; round=" + _catchUp.Snapshot().Round +
                "; delayMs=" + delayMs);

        void ICatchUpContinueReconnectHost.LogCatchUpContinue(string message) => _log(message);

        void ICatchUpContinueReconnectHost.StopConnectionHealthMonitor(string reason) =>
            _stopHealthMonitor(reason);

        void ICatchUpContinueReconnectHost.TearDownSocketForReconnect() => _tearDownSocket();

        void ICatchUpContinueReconnectHost.ScheduleAutoReconnect(string reason) =>
            _scheduleAutoReconnect(reason);

        void ICatchUpContinueReconnectHost.RecordCatchUpContinueException(
            Exception error,
            string triggerReason) =>
            _diagnostics.RecordException(
                "history",
                "catch-up-continue-failed",
                error,
                triggerReason);

        void ICatchUpContinueReconnectHost.FireAndForgetCatchUpContinue(Func<Task> work) =>
            _ = Task.Run(work);
    }
}
