// =============================================================================
// IStartBackgroundHistoryBackfillHost
//
// Lock / active flag for StartBackgroundHistoryBackfillDispatch.
// =============================================================================
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IStartBackgroundHistoryBackfillHost
    {
        Task<bool> TryAcquireHistoryHydrationAsync();

        void ReleaseHistoryHydration();

        void SetHistoryHydrationActive(int value);

        void LogHistoryHydrationSkippedAlreadyRunning(string reason);

        Task<int> RunHistoryHydrationBackfillCoreAsync(string reason);
    }
}
