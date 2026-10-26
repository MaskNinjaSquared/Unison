using System;
using System.Threading;
using System.Threading.Tasks;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// What <c>ConnectionHealthMonitor</c> asks of the connection client each tick.
    /// The monitored socket stays on the host — Core never names transport types.
    /// </summary>
    public interface IConnectionHealthMonitorHost
    {
        bool ShouldAbortLoop { get; }

        bool IsMonitoredSocketCurrent { get; }

        bool IsMonitoredSocketConnected { get; }

        Task DelayCheckIntervalAsync(CancellationToken token);

        /// <summary>Restart the app-level incoming pump when it has stalled on one stage.</summary>
        void RestartIncomingPumpIfStalled();

        bool IsNodeProcessingStalled { get; }

        bool HasFreshBackgroundConnection { get; }

        bool IsUsyncGateHeld { get; }

        bool IsHistoryCatchUpActive { get; }

        Task<bool> ProbeBackgroundAsync();

        int QueuedNodeProcessingCount { get; }

        int PendingQueryCount { get; }

        void ForceDisconnectMonitoredSocket();

        void RequestSoftReconnect(string reason);

        void WriteHealthJournal(string eventName, string detail);

        void LogHealth(string message);

        void RecordHealthException(Exception error);

        /// <summary>Null when the exception is not an explicit logout stream code.</summary>
        string TryGetExplicitLogoutStreamCode(Exception error);

        void LatchFatalAndReportStream(string code);
    }
}
