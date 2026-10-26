// =============================================================================
// IStopPeriodicFullHistorySafetyHost
//
// Side effects for StopPeriodicFullHistorySafetyDispatch.
// =============================================================================

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IStopPeriodicFullHistorySafetyHost
    {
        /// <summary>True when a CTS was present and cancelled.</summary>
        bool TryCancelPeriodicFullHistorySafety();

        void LogPeriodicFullHistorySafetyStopped(string reason);
    }
}
