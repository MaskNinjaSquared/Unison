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
using Windows.UI.Core;

namespace Unison.Uwp.UI.Helpers
{
    internal static class ChatOpenPaintSchedule
    {
        public static readonly TimeSpan TimelineMaterializeDelay = TimeSpan.FromSeconds(2);

        public static readonly TimeSpan MobileSurfacePaintDelay = TimeSpan.FromMilliseconds(48);

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
