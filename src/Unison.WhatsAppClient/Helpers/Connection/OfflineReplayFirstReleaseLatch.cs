// =============================================================================
// OfflineReplayFirstReleaseLatch
//
// First offline-replay release per open cycle. Reset on every new open so a
// stuck cycle that never reached synced can still settle on the next connect.
// =============================================================================
using System.Threading;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class OfflineReplayFirstReleaseLatch
    {
        private int _released;

        public bool TryMarkFirstRelease() =>
            Interlocked.Exchange(ref _released, 1) == 0;

        public void ResetForNewOpen() =>
            Interlocked.Exchange(ref _released, 0);

        public bool HasReleased =>
            Volatile.Read(ref _released) != 0;
    }
}
