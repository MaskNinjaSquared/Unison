// =============================================================================
// IStartConnectionHealthMonitorHost
//
// Side effects for StartConnectionHealthMonitorDispatch.
// =============================================================================

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IStartConnectionHealthMonitorHost
    {
        void StopConnectionHealthMonitor(string reason);

        void StartConnectionHealthLoop(object socket);

        void StartPeriodicFullHistorySafety();
    }
}
