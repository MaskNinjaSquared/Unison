// =============================================================================
// IForceDisconnectMonitoredSocketHost
//
// Side effects for ForceDisconnectMonitoredSocketDispatch (health monitor).
// =============================================================================

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IForceDisconnectMonitoredSocketHost
    {
        bool IsMonitoredSocketCurrent { get; }

        void DisconnectMonitoredSocketBestEffort();

        void ClearCurrentSocketReferenceIfStillMonitored();
    }
}
