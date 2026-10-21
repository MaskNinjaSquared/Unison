using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class CameraPreviewCropMathTests
    {
        [Fact]
        public void Matching_aspect_returns_full_source()
        {
            var crop = CameraPreviewCropMath.ComputeUniformToFillCrop(1200, 1600, 360, 480);
            Assert.True(crop.CoversSource(1200, 1600));
        }

        [Fact]
        public void Wider_source_crops_left_and_right()
        {
            // 16:9 source into 9:16 viewport → keep full height, crop width.
            var crop = CameraPreviewCropMath.ComputeUniformToFillCrop(1920, 1080, 360, 640);
            Assert.Equal(0, crop.Y);
            Assert.Equal(1080, crop.Height);
            Assert.True(crop.Width < 1920);
            Assert.Equal((1920 - crop.Width) / 2, crop.X);
        }

        [Fact]
        public void Taller_source_crops_top_and_bottom()
        {
            // 3:4 source into 16:9 viewport → keep full width, crop height.
            var crop = CameraPreviewCropMath.ComputeUniformToFillCrop(1200, 1600, 640, 360);
            Assert.Equal(0, crop.X);
            Assert.Equal(1200, crop.Width);
            Assert.True(crop.Height < 1600);
            Assert.Equal((1600 - crop.Height) / 2, crop.Y);
        }

        [Fact]
        public void Invalid_viewport_returns_full_source()
        {
            var crop = CameraPreviewCropMath.ComputeUniformToFillCrop(800, 600, 0, 100);
            Assert.True(crop.CoversSource(800, 600));
        }
    }
}
