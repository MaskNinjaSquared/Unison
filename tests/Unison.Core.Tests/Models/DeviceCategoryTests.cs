using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Models
{
    public sealed class DeviceCategoryTests
    {
        private const ulong MB = 1024UL * 1024UL;

        [Theory]
        [InlineData(185UL)]   // 512 MB phone
        [InlineData(287UL)]
        [InlineData(0UL)]
        public void A_512MB_class_limit_is_unsupported(ulong limitMb)
        {
            Assert.Equal(DeviceCategory.Unsupported, DeviceCategories.FromAppMemoryLimit(limitMb * MB));
        }

        [Theory]
        [InlineData(288UL)]
        [InlineData(390UL)]   // 1 GB phone
        [InlineData(644UL)]
        public void A_1GB_class_limit_is_low(ulong limitMb)
        {
            Assert.Equal(DeviceCategory.Low, DeviceCategories.FromAppMemoryLimit(limitMb * MB));
        }

        [Theory]
        [InlineData(645UL)]
        [InlineData(900UL)]   // 2 GB phone
        [InlineData(1400UL)]  // 3 GB phone
        [InlineData(16384UL)] // desktop
        public void A_2GB_class_limit_or_more_is_high(ulong limitMb)
        {
            Assert.Equal(DeviceCategory.High, DeviceCategories.FromAppMemoryLimit(limitMb * MB));
        }

        [Fact]
        public void A_desktop_without_a_real_cap_is_high()
        {
            Assert.Equal(DeviceCategory.High, DeviceCategories.FromAppMemoryLimit(ulong.MaxValue));
        }

        [Fact]
        public void Mobile_with_a_2GB_limit_is_capped_to_low()
        {
            // 900 MB app limit is High by floor, but Creators Mobile cannot survive the High
            // FULL_HISTORY cascade — same class of phone that worked on 1.8.3.
            DeviceCategory raw = DeviceCategories.FromAppMemoryLimit(900UL * MB);
            Assert.Equal(DeviceCategory.High, raw);
            Assert.Equal(DeviceCategory.Low, DeviceCategories.CapForMobile(raw, isMobile: true));
        }

        [Fact]
        public void Desktop_keeps_high_when_the_limit_is_high()
        {
            DeviceCategory raw = DeviceCategories.FromAppMemoryLimit(900UL * MB);
            Assert.Equal(DeviceCategory.High, DeviceCategories.CapForMobile(raw, isMobile: false));
        }

        [Fact]
        public void Mobile_cap_does_not_raise_unsupported()
        {
            Assert.Equal(
                DeviceCategory.Unsupported,
                DeviceCategories.CapForMobile(DeviceCategory.Unsupported, isMobile: true));
        }
    }
}
