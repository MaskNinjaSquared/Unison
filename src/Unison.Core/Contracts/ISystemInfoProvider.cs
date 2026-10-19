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
    }
}
