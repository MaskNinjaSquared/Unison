using Unison.Core.Models;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public sealed class SurfacePaintPolicyTests
    {
        [Fact]
        public void For_maps_each_category_to_its_policy()
        {
            Assert.Same(SurfacePaintPolicy.Unsupported, SurfacePaintPolicy.For(DeviceCategory.Unsupported));
            Assert.Same(SurfacePaintPolicy.Low, SurfacePaintPolicy.For(DeviceCategory.Low));
            Assert.Same(SurfacePaintPolicy.High, SurfacePaintPolicy.For(DeviceCategory.High));
        }

        [Fact]
        public void Unsupported_is_more_conservative_than_Low()
        {
            SurfacePaintPolicy unsupported = SurfacePaintPolicy.Unsupported;
            SurfacePaintPolicy low = SurfacePaintPolicy.Low;

            Assert.True(unsupported.SurfaceYieldDelayMs >= low.SurfaceYieldDelayMs);
            Assert.Equal(80, unsupported.SurfaceYieldDelayMs);
            Assert.Equal(48, low.SurfaceYieldDelayMs);
            Assert.Equal(2000, unsupported.TimelineMaterializeDelayMs);
            Assert.Equal(2000, low.TimelineMaterializeDelayMs);
            Assert.True(unsupported.ShowLoadingAfterFirstPaint);
            Assert.True(low.ShowLoadingAfterFirstPaint);
            Assert.Equal(80, unsupported.SectionPivotYieldDelayMs);
        }

        [Fact]
        public void High_skips_paint_delays()
        {
            SurfacePaintPolicy high = SurfacePaintPolicy.High;

            Assert.Equal(0, high.SurfaceYieldDelayMs);
            Assert.Equal(0, high.TimelineMaterializeDelayMs);
            Assert.Equal(0, high.SectionPivotYieldDelayMs);
            Assert.False(high.ShowLoadingAfterFirstPaint);
        }

        [Fact]
        public void ForMobileFlag_maps_mobile_to_Low_and_desktop_to_High()
        {
            Assert.Same(SurfacePaintPolicy.Low, SurfacePaintPolicy.ForMobileFlag(true));
            Assert.Same(SurfacePaintPolicy.High, SurfacePaintPolicy.ForMobileFlag(false));
        }
    }
}
