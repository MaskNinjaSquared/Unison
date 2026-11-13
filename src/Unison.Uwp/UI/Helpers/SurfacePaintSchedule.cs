// =============================================================================
// SurfacePaintSchedule
//
// Generic chrome-first yield driven by SurfacePaintPolicy (DeviceCategory).
// ChatOpenPaintSchedule keeps the chat-open-specific materialize/settle API and
// delegates surface yield here so pivot/settings/list hosts share one path.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.State;
using Windows.UI.Core;

namespace Unison.Uwp.UI.Helpers
{
    internal static class SurfacePaintSchedule
    {
        /// <summary>
        /// One Normal dispatcher turn (+ policy.SurfaceYieldDelayMs) so chrome
        /// can paint before heavy work. Normal — not Low — so we are not queued
        /// behind list layout.
        /// </summary>
        public static async Task YieldForSurfacePaintAsync(
            CoreDispatcher dispatcher,
            SurfacePaintPolicy policy)
        {
            if (dispatcher == null)
            {
                return;
            }

            await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => { });

            int delayMs = policy?.SurfaceYieldDelayMs ?? 0;
            if (delayMs > 0)
            {
                await Task.Delay(delayMs);
            }
        }

        /// <summary>
        /// Pivot / section construction yield (Legacy SectionLoadingOverlay path).
        /// </summary>
        public static async Task YieldForSectionPaintAsync(SurfacePaintPolicy policy)
        {
            int delayMs = policy?.SectionPivotYieldDelayMs ?? 0;
            if (delayMs > 0)
            {
                await Task.Delay(delayMs);
            }
        }
    }
}
