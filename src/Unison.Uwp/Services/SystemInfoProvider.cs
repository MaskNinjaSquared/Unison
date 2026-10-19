using System;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Windows.System;
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
        private readonly Lazy<DeviceCategory> _deviceCategory = new Lazy<DeviceCategory>(DetectDeviceCategory);

        public bool IsMobile() => DetectIsMobile();

        public DeviceCategory GetDeviceCategory() => _deviceCategory.Value;

        private static DeviceCategory DetectDeviceCategory()
        {
            bool mobile = DetectIsMobile();
            try
            {
                return DeviceCategories.CapForMobile(
                    DeviceCategories.FromAppMemoryLimit(MemoryManager.AppMemoryUsageLimit),
                    mobile);
            }
            catch
            {
                return mobile ? DeviceCategory.Low : DeviceCategory.High;
            }
        }

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
