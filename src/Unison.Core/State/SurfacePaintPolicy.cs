// =============================================================================
// SurfacePaintPolicy
//
// How long to wait after chrome paints before heavy work (SQLite, list fill,
// section content) on each DeviceCategory. Pure numbers — no dispatcher.
// UWP SurfacePaintSchedule / ChatOpenPaintSchedule consume this.
// =============================================================================
using System;
using Unison.Core.Models;

namespace Unison.Core.State
{
    public sealed class SurfacePaintPolicy
    {
        private SurfacePaintPolicy(
            DeviceCategory category,
            int surfaceYieldDelayMs,
            int timelineMaterializeDelayMs,
            bool showLoadingAfterFirstPaint,
            int sectionPivotYieldDelayMs)
        {
            Category = category;
            SurfaceYieldDelayMs = surfaceYieldDelayMs;
            TimelineMaterializeDelayMs = timelineMaterializeDelayMs;
            ShowLoadingAfterFirstPaint = showLoadingAfterFirstPaint;
            SectionPivotYieldDelayMs = sectionPivotYieldDelayMs;
        }

        public DeviceCategory Category { get; }

        /// <summary>
        /// Extra pause after one Normal dispatcher turn so header/pane can paint
        /// before SQLite or list materialize. Zero on High.
        /// </summary>
        public int SurfaceYieldDelayMs { get; }

        /// <summary>
        /// Mobile-style pause after ProgressBar before timeline bubbles. Zero skips.
        /// </summary>
        public int TimelineMaterializeDelayMs { get; }

        /// <summary>
        /// When true, hosts show IsLoading only after the first chrome frame
        /// (XAML pure first), then fill. High paints and loads together.
        /// </summary>
        public bool ShowLoadingAfterFirstPaint { get; }

        /// <summary>
        /// Legacy pivot section yield before constructing the section page.
        /// </summary>
        public int SectionPivotYieldDelayMs { get; }

        public static SurfacePaintPolicy For(DeviceCategory category)
        {
            switch (category)
            {
                case DeviceCategory.Unsupported:
                    return Unsupported;
                case DeviceCategory.Low:
                    return Low;
                case DeviceCategory.High:
                    return High;
                default:
                    return Low;
            }
        }

        /// <summary>
        /// Maps the legacy IsMobile boolean used by ChatOpenPaintSchedule call sites
        /// onto Low (Mobile) vs High (desktop) until callers pass DeviceCategory.
        /// </summary>
        public static SurfacePaintPolicy ForMobileFlag(bool isWindowsMobile) =>
            For(isWindowsMobile ? DeviceCategory.Low : DeviceCategory.High);

        public static readonly SurfacePaintPolicy Unsupported = new SurfacePaintPolicy(
            DeviceCategory.Unsupported,
            surfaceYieldDelayMs: 80,
            timelineMaterializeDelayMs: 2000,
            showLoadingAfterFirstPaint: true,
            sectionPivotYieldDelayMs: 80);

        public static readonly SurfacePaintPolicy Low = new SurfacePaintPolicy(
            DeviceCategory.Low,
            surfaceYieldDelayMs: 48,
            timelineMaterializeDelayMs: 2000,
            showLoadingAfterFirstPaint: true,
            sectionPivotYieldDelayMs: 80);

        public static readonly SurfacePaintPolicy High = new SurfacePaintPolicy(
            DeviceCategory.High,
            surfaceYieldDelayMs: 0,
            timelineMaterializeDelayMs: 0,
            showLoadingAfterFirstPaint: false,
            sectionPivotYieldDelayMs: 0);

        public TimeSpan SurfaceYieldDelay => TimeSpan.FromMilliseconds(SurfaceYieldDelayMs);

        public TimeSpan TimelineMaterializeDelay => TimeSpan.FromMilliseconds(TimelineMaterializeDelayMs);

        public TimeSpan SectionPivotYieldDelay => TimeSpan.FromMilliseconds(SectionPivotYieldDelayMs);
    }
}
