using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Device / SKU helpers (Imgur <c>ISystemInfoProvider</c> pattern).
    /// </summary>
    public interface ISystemInfoProvider
    {
        /// <summary>True on Windows 10 Mobile (Windows.Mobile).</summary>
        bool IsMobile();

        /// <summary>
        /// Mobile Continuum (phone + mouse / docked desktop shell). Treat like desktop chrome.
        /// </summary>
        bool IsContinuum();

        /// <summary>
        /// The device's memory tier, read from the per-app memory limit once, in the foreground,
        /// and kept for the process: the limit drops while the app is in the background.
        /// </summary>
        DeviceCategory GetDeviceCategory();

        /// <summary>
        /// True when Start Live Tiles / <c>SecondaryTile</c> pin UX is still useful (Windows 10
        /// and Mobile). False on Windows 11 (build ≥ 22000), where classic Live Tiles are gone.
        /// </summary>
        bool LiveTilesCompatible();
    }
}
