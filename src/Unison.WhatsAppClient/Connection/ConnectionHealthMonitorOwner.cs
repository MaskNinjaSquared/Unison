// =============================================================================
// ConnectionHealthMonitorOwner
//
// Owns connection-health and periodic FULL_HISTORY safety CTS / Task.Run slots.
// WhatsAppService only supplies loop bodies via IConnectionHealthMonitorIoHost
// (same shape as SoftReconnectCoordinator / OfflineReplayFlush).
// =============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;

namespace Unison.WhatsAppClient.Connection
{
    public sealed class ConnectionHealthMonitorOwner :
        IStartConnectionHealthMonitorHost,
        IStopConnectionHealthMonitorHost,
        IStartPeriodicFullHistorySafetyHost,
        IStopPeriodicFullHistorySafetyHost
    {
        private readonly IConnectionHealthMonitorIoHost _io;
        private CancellationTokenSource _healthCts;
        private Task _healthTask = Task.CompletedTask;
        private CancellationTokenSource _periodicCts;
        private Task _periodicTask = Task.CompletedTask;

        public ConnectionHealthMonitorOwner(IConnectionHealthMonitorIoHost io)
        {
            _io = io ?? throw new ArgumentNullException(nameof(io));
        }

        /// <summary>True while a health-monitor CTS is armed.</summary>
        public bool IsHealthMonitorArmed => Volatile.Read(ref _healthCts) != null;

        /// <summary>True while the periodic FULL_HISTORY safety CTS is armed.</summary>
        public bool IsPeriodicSafetyArmed => Volatile.Read(ref _periodicCts) != null;

        public void Start(object socket) =>
            StartConnectionHealthMonitorDispatch.Run(this, socket);

        public void Stop(string reason) =>
            StopConnectionHealthMonitorDispatch.Run(this, reason);

        public void StartPeriodicSafety() =>
            StartPeriodicFullHistorySafetyDispatch.Run(this);

        public void StopPeriodicSafety(string reason) =>
            StopPeriodicFullHistorySafetyDispatch.Run(this, reason);

        // --- IStartConnectionHealthMonitorHost -------------------------------

        void IStartConnectionHealthMonitorHost.StopConnectionHealthMonitor(string reason) =>
            Stop(reason);

        void IStartConnectionHealthMonitorHost.StartConnectionHealthLoop(object socket)
        {
            if (socket == null)
            {
                return;
            }

            var cts = new CancellationTokenSource();
            _healthCts = cts;
            CancellationToken token = cts.Token;
            _healthTask = Task.Run(() => _io.RunHealthLoopAsync(socket, token));
        }

        void IStartConnectionHealthMonitorHost.StartPeriodicFullHistorySafety() =>
            StartPeriodicSafety();

        // --- IStopConnectionHealthMonitorHost --------------------------------

        bool IStopConnectionHealthMonitorHost.TryCancelConnectionHealthMonitor()
        {
            CancellationTokenSource cts = Interlocked.Exchange(ref _healthCts, null);
            if (cts == null)
            {
                return false;
            }

            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch
            {
            }

            return true;
        }

        void IStopConnectionHealthMonitorHost.LogConnectionHealthMonitorStopped(string reason) =>
            Debug.WriteLine(
                "[ConnectionHealthMonitorOwner] Connection health monitor stopped: " +
                (reason ?? string.Empty));

        void IStopConnectionHealthMonitorHost.StopPeriodicFullHistorySafety(string reason) =>
            StopPeriodicSafety(reason);

        // --- IStartPeriodicFullHistorySafetyHost -----------------------------

        void IStartPeriodicFullHistorySafetyHost.StopPeriodicFullHistorySafety(string reason) =>
            StopPeriodicSafety(reason);

        void IStartPeriodicFullHistorySafetyHost.StartPeriodicFullHistorySafetyLoop()
        {
            var cts = new CancellationTokenSource();
            _periodicCts = cts;
            CancellationToken token = cts.Token;
            _periodicTask = Task.Run(() => _io.RunPeriodicFullHistorySafetyLoopAsync(token));
        }

        double IStartPeriodicFullHistorySafetyHost.PeriodicFullHistorySafetyIntervalMinutes =>
            HistoryPeriodicFullSafetyDecision.Interval.TotalMinutes;

        void IStartPeriodicFullHistorySafetyHost.LogPeriodicFullHistorySafetyStarted(
            double intervalMinutes) =>
            Debug.WriteLine(
                "[ConnectionHealthMonitorOwner] Periodic FULL_HISTORY safety started; intervalMinutes=" +
                intervalMinutes.ToString("F0"));

        // --- IStopPeriodicFullHistorySafetyHost ------------------------------

        bool IStopPeriodicFullHistorySafetyHost.TryCancelPeriodicFullHistorySafety()
        {
            CancellationTokenSource cts = Interlocked.Exchange(ref _periodicCts, null);
            if (cts == null)
            {
                return false;
            }

            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch
            {
            }

            return true;
        }

        void IStopPeriodicFullHistorySafetyHost.LogPeriodicFullHistorySafetyStopped(string reason) =>
            Debug.WriteLine(
                "[ConnectionHealthMonitorOwner] Periodic FULL_HISTORY safety stopped: " +
                (reason ?? string.Empty));
    }
}
