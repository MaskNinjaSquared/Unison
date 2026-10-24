// =============================================================================
// ChatTimelineLoadPriorityGate
//
// Held while CompleteActiveChatLoadAsync runs (especially Mobile toast/deep-link).
// ChatPreviewReconciler pauses tip-swapped UI slices so SQLite timeline load is
// not starved by hundreds of PropertyChanged notifications on the dispatcher.
// =============================================================================
using System;
using System.Diagnostics;
using System.Threading;

namespace Unison.Core.State
{
    public sealed class ChatTimelineLoadPriorityGate
    {
        private int _held;

        public bool IsHeld => Volatile.Read(ref _held) != 0;

        public event EventHandler Changed;

        public void Hold(string reason)
        {
            if (Interlocked.Exchange(ref _held, 1) == 1)
            {
                return;
            }

            Debug.WriteLine("[ChatTimelineLoadPriorityGate] Hold (" + (reason ?? string.Empty) + ")");
            RaiseChanged();
        }

        public void Release(string reason)
        {
            if (Interlocked.Exchange(ref _held, 0) == 0)
            {
                return;
            }

            Debug.WriteLine("[ChatTimelineLoadPriorityGate] Release (" + (reason ?? string.Empty) + ")");
            RaiseChanged();
        }

        private void RaiseChanged()
        {
            try
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatTimelineLoadPriorityGate] Listener failed: " + ex.Message);
            }
        }
    }
}
