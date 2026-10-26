// =============================================================================
// IStartPeriodicFullHistorySafetyHost
//
// Side effects for StartPeriodicFullHistorySafetyDispatch.
// =============================================================================

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IStartPeriodicFullHistorySafetyHost
    {
        void StopPeriodicFullHistorySafety(string reason);

        void StartPeriodicFullHistorySafetyLoop();

        double PeriodicFullHistorySafetyIntervalMinutes { get; }

        void LogPeriodicFullHistorySafetyStarted(double intervalMinutes);
    }
}
