// =============================================================================
// DeferredStartupOnce
//
// Owns the once-per-session CAS that used to live on WhatsAppService as
// _deferredStartupMaintenanceStarted. StartDeferredStartupMaintenance claims
// the slot; a second call is a no-op.
// =============================================================================
using System.Threading;

namespace Unison.WhatsAppClient.Connection
{
    public sealed class DeferredStartupOnce
    {
        private int _started;

        /// <summary>
        /// Returns true the first time; false if already claimed this session.
        /// </summary>
        public bool TryStart() => Interlocked.Exchange(ref _started, 1) == 0;
    }
}
