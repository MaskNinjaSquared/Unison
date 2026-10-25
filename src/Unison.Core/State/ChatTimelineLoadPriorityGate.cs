// =============================================================================
// ChatTimelineLoadPriorityGate
//
// Ref-counted hold while Prepare/Complete open a chat (especially Mobile).
// ChatPreviewReconciler pauses tip-swapped UI slices; group roster / metadata
// appliers wait so a 500+ member apply cannot starve Yield + LoadMessages.
// =============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Unison.Core.State
{
    public sealed class ChatTimelineLoadPriorityGate
    {
        private const int WaitPollMs = 40;

        private int _held;

        public bool IsHeld => Volatile.Read(ref _held) > 0;

        public event EventHandler Changed;

        public void Hold(string reason)
        {
            int after = Interlocked.Increment(ref _held);
            if (after == 1)
            {
                Debug.WriteLine("[ChatTimelineLoadPriorityGate] Hold (" + (reason ?? string.Empty) + ")");
                RaiseChanged();
            }
        }

        public void Release(string reason)
        {
            int after = Interlocked.Decrement(ref _held);
            if (after < 0)
            {
                Interlocked.Exchange(ref _held, 0);
                after = 0;
            }

            if (after == 0)
            {
                Debug.WriteLine("[ChatTimelineLoadPriorityGate] Release (" + (reason ?? string.Empty) + ")");
                RaiseChanged();
            }
        }

        /// <summary>
        /// Spins until no hold remains. Used by roster/metadata UI applies so they
        /// run after the open timeline has painted.
        /// </summary>
        public async Task WaitWhileHeldAsync()
        {
            while (IsHeld)
            {
                await Task.Delay(WaitPollMs).ConfigureAwait(false);
            }
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
