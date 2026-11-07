// =============================================================================
// ChatTimelineLoadPriorityGate
//
// Ref-counted hold while Prepare/Complete open a chat (especially Mobile).
// ChatPreviewReconciler pauses tip-swapped UI slices; group roster / metadata
// appliers wait so a 500+ member apply cannot starve Yield + LoadMessages.
//
// After Release, waiters are admitted one-at-a-time with a gap so ListView unload
// / ClearRecordingUi navigation can finish before ApplyMetadata stamps the UI
// (cfb6fa4f resume: groups stuck 3→4/12 for ~133s under active=none).
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

        /// <summary>Minimum gap between post-release admits (milliseconds).</summary>
        public const int PostReleaseAdmitGapMs = 80;

        private int _held;
        private long _nextAdmitUtcTicks;

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
        /// Spins until no hold remains, then takes a staggered admit slot so concurrent
        /// waiters do not all hit the dispatcher on the same turn after Release.
        /// </summary>
        public async Task WaitWhileHeldAsync()
        {
            while (IsHeld)
            {
                await Task.Delay(WaitPollMs).ConfigureAwait(false);
            }

            await AdmitAfterReleaseAsync().ConfigureAwait(false);
        }

        private async Task AdmitAfterReleaseAsync()
        {
            long gapTicks = TimeSpan.FromMilliseconds(PostReleaseAdmitGapMs).Ticks;
            while (true)
            {
                // Re-check hold: another open can land while we wait for an admit slot.
                if (IsHeld)
                {
                    while (IsHeld)
                    {
                        await Task.Delay(WaitPollMs).ConfigureAwait(false);
                    }

                    continue;
                }

                long now = DateTime.UtcNow.Ticks;
                long next = Interlocked.Read(ref _nextAdmitUtcTicks);
                if (now >= next)
                {
                    long candidate = now + gapTicks;
                    if (Interlocked.CompareExchange(ref _nextAdmitUtcTicks, candidate, next) == next)
                    {
                        return;
                    }

                    continue;
                }

                int waitMs = (int)Math.Max(
                    WaitPollMs,
                    TimeSpan.FromTicks(next - now).TotalMilliseconds);
                await Task.Delay(Math.Min(waitMs, PostReleaseAdmitGapMs)).ConfigureAwait(false);
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
