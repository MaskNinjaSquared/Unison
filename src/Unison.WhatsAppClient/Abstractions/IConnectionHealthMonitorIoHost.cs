// =============================================================================
// IConnectionHealthMonitorIoHost
//
// Loop bodies for ConnectionHealthMonitorOwner. CTS / Task.Run live on the owner.
// =============================================================================
using System.Threading;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IConnectionHealthMonitorIoHost
    {
        Task RunHealthLoopAsync(object socket, CancellationToken token);

        Task RunPeriodicFullHistorySafetyLoopAsync(CancellationToken token);
    }
}
