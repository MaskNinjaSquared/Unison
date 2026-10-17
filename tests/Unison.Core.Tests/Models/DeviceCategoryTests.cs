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
    }
}
