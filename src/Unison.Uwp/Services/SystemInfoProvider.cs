using Unison.Core.Contracts;
using Windows.System.Profile;
using Windows.UI.ViewManagement;

namespace Unison.Uwp.Services
{
    /// <summary>
    /// Platform SKU checks (mirrors Imgur <c>SystemInfoProvider</c>).
    /// Raw DeviceFamily / interaction-mode access lives only here.
    /// </summary>
    public sealed class SystemInfoProvider : ISystemInfoProvider
    {
        public bool IsMobile() => DetectIsMobile();

        public bool IsContinuum()
        {
            try
            {
                // Imgur: Mobile + mouse interaction (Continuum / docking).
                return DetectIsMobile() &&
                       UIViewSettings.GetForCurrentView().UserInteractionMode == UserInteractionMode.Mouse;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// For static / XAML control constructors where DI is not yet available.
        /// </summary>
        public static bool DetectIsMobile()
        {
            try
            {
                return string.Equals(
                    AnalyticsInfo.VersionInfo.DeviceFamily,
                    "Windows.Mobile",
                    System.StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
