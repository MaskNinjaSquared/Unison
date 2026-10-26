// =============================================================================
// IPublishInitialSyncProgressHost
//
// Side effects for PublishInitialSyncProgressDispatch.
// =============================================================================
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IPublishInitialSyncProgressHost
    {
        bool FirstSyncListAlreadyReleased { get; set; }

        bool FirstSyncListHoldOwned { get; set; }

        bool FirstSyncListReleaseDeferred { get; set; }

        void WriteInitialSyncProgressJournal(
            bool active,
            bool completed,
            int processed,
            int total,
            string stage);

        void PublishInitialSyncChannel(
            bool active,
            bool completed,
            int processed,
            int total,
            string stage);

        void FireAndForgetFlushDiagnosticsOnComplete();
    }
}
