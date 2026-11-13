// =============================================================================
// SurfacePaintSchedule
//
// Generic chrome-first yield driven by SurfacePaintPolicy (DeviceCategory).
// ChatOpenPaintSchedule keeps the chat-open-specific materialize/settle API and
// delegates surface yield here so pivot/settings/list hosts share one path.
// =============================================================================
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.Core.State;
using Windows.UI.Core;

namespace Unison.Uwp.UI.Helpers
{
    internal static class SurfacePaintSchedule
    {
        /// <summary>
        /// Resolves policy from <see cref="ISystemInfoProvider.GetDeviceCategory"/>
        /// (Unsupported vs Low vs High), not a mobile boolean.
        /// </summary>
        public static SurfacePaintPolicy ResolvePolicy()
        {
            try
            {
                ISystemInfoProvider info = App.Services?.GetService<ISystemInfoProvider>();
                if (info != null)
                {
                    return SurfacePaintPolicy.For(info.GetDeviceCategory());
                }
            }
            catch
            {
            }

            return SurfacePaintPolicy.Low;
        }

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

        public static Task YieldForSurfacePaintAsync(CoreDispatcher dispatcher) =>
            YieldForSurfacePaintAsync(dispatcher, ResolvePolicy());

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

        public static Task YieldForSectionPaintAsync() =>
            YieldForSectionPaintAsync(ResolvePolicy());
    }
}
