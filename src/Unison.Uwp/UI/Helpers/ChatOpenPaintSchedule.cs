// =============================================================================
// ChatOpenPaintSchedule
//
// Shared list→detail paint yield + Mobile materialize delay. All ChatDetailView
// shells call this so Normal yield / PreferFast skip cannot drift again.
// Delays come from SurfacePaintPolicy (DeviceCategory); bool isWindowsMobile
// overloads map to Low vs High for existing call sites.
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Helpers.Chats;
using Unison.Core.Models;
using Unison.Core.State;
using Windows.UI.Core;

namespace Unison.Uwp.UI.Helpers
{
    internal static class ChatOpenPaintSchedule
    {
        public static readonly TimeSpan TimelineMaterializeDelay =
            SurfacePaintPolicy.Low.TimelineMaterializeDelay;

        public static readonly TimeSpan MobileSurfacePaintDelay =
            SurfacePaintPolicy.Low.SurfaceYieldDelay;

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
        public static Task ReleaseTimelinePriorityAfterSettleAsync(
            ChatTimelineLoadPriorityGate gate,
            bool isWindowsMobile,
            string reason = "prepare-active-chat") =>
            ReleaseTimelinePriorityAfterSettleAsync(
                gate,
                SurfacePaintPolicy.ForMobileFlag(isWindowsMobile),
                reason);

        public static async Task ReleaseTimelinePriorityAfterSettleAsync(
            ChatTimelineLoadPriorityGate gate,
            SurfacePaintPolicy policy,
            string reason = "prepare-active-chat")
        {
            if (gate == null)
            {
                return;
            }

            bool isWindowsMobile = policy != null &&
                policy.Category != DeviceCategory.High;

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
        /// One Normal dispatcher turn (+ surface yield on Low/Unsupported) so header/pane
        /// can paint before SQLite.
        /// </summary>
        public static Task YieldForSurfacePaintAsync(
            CoreDispatcher dispatcher,
            bool isWindowsMobile) =>
            SurfacePaintSchedule.YieldForSurfacePaintAsync(
                dispatcher,
                SurfacePaintPolicy.ForMobileFlag(isWindowsMobile));

        public static Task YieldForSurfacePaintAsync(
            CoreDispatcher dispatcher,
            SurfacePaintPolicy policy) =>
            SurfacePaintSchedule.YieldForSurfacePaintAsync(dispatcher, policy);

        public static Task YieldForSurfacePaintAsync(
            CoreDispatcher dispatcher,
            DeviceCategory category) =>
            SurfacePaintSchedule.YieldForSurfacePaintAsync(
                dispatcher,
                SurfacePaintPolicy.For(category));

        /// <summary>
        /// Materialize pause after ProgressBar, unless PreferFast / deep-link hold.
        /// Returns false when cancelled.
        /// </summary>
        public static Task<bool> AwaitMaterializeDelayAsync(
            bool isWindowsMobile,
            bool preferFastTimelineLoad,
            bool deepLinkListHeld,
            CancellationToken token,
            Action onSkipped = null,
            Action onWaiting = null) =>
            AwaitMaterializeDelayAsync(
                SurfacePaintPolicy.ForMobileFlag(isWindowsMobile),
                preferFastTimelineLoad,
                deepLinkListHeld,
                token,
                onSkipped,
                onWaiting);

        public static async Task<bool> AwaitMaterializeDelayAsync(
            SurfacePaintPolicy policy,
            bool preferFastTimelineLoad,
            bool deepLinkListHeld,
            CancellationToken token,
            Action onSkipped = null,
            Action onWaiting = null)
        {
            bool isWindowsMobile = policy != null &&
                policy.Category != DeviceCategory.High;

            if (ChatTimelineOpenPriority.ShouldSkipMaterializeDelay(
                    isWindowsMobile,
                    preferFastTimelineLoad,
                    deepLinkListHeld))
            {
                onSkipped?.Invoke();
                return !token.IsCancellationRequested;
            }

            int delayMs = policy?.TimelineMaterializeDelayMs ?? 0;
            if (delayMs <= 0)
            {
                onSkipped?.Invoke();
                return !token.IsCancellationRequested;
            }

            try
            {
                onWaiting?.Invoke();
                await Task.Delay(delayMs, token).ConfigureAwait(false);
                return !token.IsCancellationRequested;
            }
            catch (TaskCanceledException)
            {
                return false;
            }
        }
    }
}
