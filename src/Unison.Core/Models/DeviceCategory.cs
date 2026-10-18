namespace Unison.Core.Models
{
    /// <summary>
    /// How much memory the system lets Unison use on this device, grouped into the RAM tiers
    /// Windows 10 hands out per-app limits for. Ordered: a higher value has more room.
    /// </summary>
    public enum DeviceCategory
    {
        /// <summary>A 512 MB class device (limit around 185 MB).</summary>
        Unsupported = 0,

        /// <summary>A 1 GB class device (limit around 390 MB).</summary>
        Low = 1,

        /// <summary>A 2 GB class device or bigger (limit around 900 MB and up, desktop included).</summary>
        High = 2
    }

    public static class DeviceCategories
    {
        private const ulong Megabyte = 1024UL * 1024UL;

        // Midway between the limits Windows gives each tier (185 / 390 / 900 MB), so a few MB of
        // drift between OS builds does not move a device across a line.
        public const ulong LowFloorBytes = 288UL * Megabyte;
        public const ulong HighFloorBytes = 645UL * Megabyte;

        public static DeviceCategory FromAppMemoryLimit(ulong limitBytes)
        {
            if (limitBytes >= HighFloorBytes)
            {
                return DeviceCategory.High;
            }

            return limitBytes >= LowFloorBytes ? DeviceCategory.Low : DeviceCategory.Unsupported;
        }

        /// <summary>
        /// Windows Phone with a 2 GB SKU still reports ~900 MB app limit (High by floor), but the
        /// High sync cascade (adjacent FULL_HISTORY + parallel enrich) OOMs Creators Mobile.
        /// Cap Mobile at Low so workload matches 1.8.3-era "one quiet lot, then decorate".
        /// </summary>
        public static DeviceCategory CapForMobile(DeviceCategory category, bool isMobile)
        {
            if (isMobile && category == DeviceCategory.High)
            {
                return DeviceCategory.Low;
            }

            return category;
        }
    }
}
