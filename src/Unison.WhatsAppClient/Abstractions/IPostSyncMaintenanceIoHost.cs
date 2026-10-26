// =============================================================================
// IPostSyncMaintenanceIoHost
//
// Quiet/memory/façade side effects for PostSyncMaintenanceOwner.
// CTS / CAS / Task.Run live on the owner.
// =============================================================================
using System.Threading;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IPostSyncMaintenanceIoHost
    {
        Task RunPostReplayMaintenanceAsync(int offlineCount, CancellationToken token);

        Task RunPostMessageEnrichmentAsync(string reason, CancellationToken token);

        void ClearEnrichmentAwaitingHeavyHistory();

        void LogPostMessageEnrichment(string message);

        void WritePostMessageEnrichmentScheduled(string reason);
    }
}
