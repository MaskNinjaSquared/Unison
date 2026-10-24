// =============================================================================
// IStopConnectionHealthMonitorHost
//
// Side effects for StopConnectionHealthMonitorDispatch.
// =============================================================================

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IStopConnectionHealthMonitorHost
    {
        /// <summary>True when a health CTS was present and cancelled.</summary>
        bool TryCancelConnectionHealthMonitor();

        void LogConnectionHealthMonitorStopped(string reason);

        void StopPeriodicFullHistorySafety(string reason);
    }
}
