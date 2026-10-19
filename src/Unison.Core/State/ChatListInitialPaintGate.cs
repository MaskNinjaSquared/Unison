// =============================================================================
// ChatListInitialPaintGate
//
// After the first history quiet-finalize the list still materializes and releases
// rows. Post-sync name/avatar/group enrichment must wait until that paint finishes
// (or the list was never held), or Mobile dies under the overlap.
// =============================================================================
using System;
using System.Diagnostics;
using System.Threading;

namespace Unison.Core.State
{
    public sealed class ChatListInitialPaintGate
    {
        private int _released = 1; // open until a first-sync hold arms the gate
        private int _armed;

        public bool IsReleased => Volatile.Read(ref _released) != 0;

        public event EventHandler Released;

        /// <summary>First-sync hold started: enrichment must wait for <see cref="MarkReleased"/>.</summary>
        public void Arm(string reason)
        {
            Interlocked.Exchange(ref _armed, 1);
            Interlocked.Exchange(ref _released, 0);
            Debug.WriteLine("[ChatListInitialPaintGate] Armed (" + (reason ?? string.Empty) + ")");
        }

        public void MarkReleased(string reason)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            Debug.WriteLine("[ChatListInitialPaintGate] Released (" + (reason ?? string.Empty) + ")");
            try
            {
                Released?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatListInitialPaintGate] Listener failed: " + ex.Message);
            }
        }

        /// <summary>Session wipe / resync: if a hold is coming, arm again from HistoryFacade.</summary>
        public void Reset(string reason)
        {
            Interlocked.Exchange(ref _armed, 0);
            Interlocked.Exchange(ref _released, 1);
            Debug.WriteLine("[ChatListInitialPaintGate] Reset (" + (reason ?? string.Empty) + ")");
        }
    }
}
