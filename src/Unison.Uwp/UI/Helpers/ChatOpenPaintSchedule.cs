// =============================================================================
// ChatOpenPaintSchedule
//
// Shared list→detail paint yield + Mobile materialize delay. All ChatDetailView
// shells call this so Normal yield / PreferFast skip cannot drift again.
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Helpers.Chats;
using Unison.Core.State;
using Windows.UI.Core;

namespace Unison.Uwp.UI.Helpers
{
    internal static class ChatOpenPaintSchedule
    {
        public static readonly TimeSpan TimelineMaterializeDelay = TimeSpan.FromSeconds(2);

        public static readonly TimeSpan MobileSurfacePaintDelay = TimeSpan.FromMilliseconds(48);

        /// <summary>
        /// After Complete paints bubbles, keep timeline priority held on Mobile so the
        /// list cannot resume refresh-visible Continues during the first scroll seconds.
        /// </summary>
        public static readonly TimeSpan MobileScrollSettleHold =
            ChatTimelineOpenPriority.MobileScrollSettleHold;

        /// <summary>
        /// Releases the prepare→complete hold. On Mobile waits
        /// <see cref="MobileScrollSettleHold"/> first so scroll stays smooth after a heavy open.
        /// </summary>
        public static async Task ReleaseTimelinePriorityAfterSettleAsync(
            ChatTimelineLoadPriorityGate gate,
            bool isWindowsMobile,
            string reason = "prepare-active-chat")
        {
            if (gate == null)
            {
                return;
            }

            if (!ChatTimelineOpenPriority.ShouldDelayTimelinePriorityRelease(isWindowsMobile))
            {
                gate.Release(reason);
                return;
            }

            try
            {
                await Task.Delay(MobileScrollSettleHold).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                // Still pair Release — gate is ref-counted and must not leak.
            }
            finally
            {
                gate.Release(reason);
            }
        }

        /// <summary>
        /// One Normal dispatcher turn (+ 48 ms on Mobile) so header/pane can paint
        /// before SQLite. Normal — not Low — so we are not queued behind list layout.
        /// </summary>
        public static async Task YieldForSurfacePaintAsync(
            CoreDispatcher dispatcher,
            bool isWindowsMobile)
        {
            if (dispatcher == null)
            {
                return;
            }

            await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => { });
            if (isWindowsMobile)
            {
                await Task.Delay(MobileSurfacePaintDelay);
            }
        }

        /// <summary>
        /// Mobile 2s pause after ProgressBar, unless PreferFast / deep-link hold.
        /// Returns false when cancelled.
        /// </summary>
        public static async Task<bool> AwaitMaterializeDelayAsync(
            bool isWindowsMobile,
            bool preferFastTimelineLoad,
            bool deepLinkListHeld,
            CancellationToken token,
            Action onSkipped = null,
            Action onWaiting = null)
        {
            if (ChatTimelineOpenPriority.ShouldSkipMaterializeDelay(
                    isWindowsMobile,
                    preferFastTimelineLoad,
                    deepLinkListHeld))
            {
                onSkipped?.Invoke();
                return !token.IsCancellationRequested;
            }

            try
            {
                onWaiting?.Invoke();
                await Task.Delay(TimelineMaterializeDelay, token).ConfigureAwait(false);
                return !token.IsCancellationRequested;
            }
            catch (TaskCanceledException)
            {
                return false;
            }
        }
    }
}
