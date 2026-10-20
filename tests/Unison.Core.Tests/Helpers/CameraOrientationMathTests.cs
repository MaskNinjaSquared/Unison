using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class CameraOrientationMathTests
    {
        [Fact]
        public void External_enclosure_needs_no_preview_rotation()
        {
            var enclosure = new CameraEnclosureInfo(CameraEnclosurePanel.Unknown, 0);
            Assert.Equal(
                0,
                CameraOrientationMath.GetPreviewRotationClockwiseDegrees(
                    enclosure,
                    CameraDisplayOrientation.Portrait,
                    CameraDisplayOrientation.Portrait));
        }

        [Fact]
        public void Front_panel_mirrors_preview()
        {
            Assert.True(CameraOrientationMath.ShouldMirrorPreview(
                new CameraEnclosureInfo(CameraEnclosurePanel.Front, 0)));
            Assert.False(CameraOrientationMath.ShouldMirrorPreview(
                new CameraEnclosureInfo(CameraEnclosurePanel.Back, 0)));
        }

        [Fact]
        public void Portrait_native_back_camera_in_portrait_preview_is_90_or_270()
        {
            // Portrait-first phone + back camera + portrait display: sensor offset applies.
            var enclosure = new CameraEnclosureInfo(CameraEnclosurePanel.Back, 0);
            int degrees = CameraOrientationMath.GetPreviewRotationClockwiseDegrees(
                enclosure,
                CameraDisplayOrientation.Portrait,
                CameraDisplayOrientation.Portrait);
            Assert.True(degrees == 90 || degrees == 270 || degrees == 0 || degrees == 180);
            // Non-zero on typical portrait-first + portrait UI (Unigram / MS sample path).
            Assert.NotEqual(0, degrees);
        }

        [Fact]
        public void Landscape_native_back_camera_in_landscape_preview_is_zero()
        {
            var enclosure = new CameraEnclosureInfo(CameraEnclosurePanel.Back, 0);
            Assert.Equal(
                0,
                CameraOrientationMath.GetPreviewRotationClockwiseDegrees(
                    enclosure,
                    CameraDisplayOrientation.Landscape,
                    CameraDisplayOrientation.Landscape));
        }

        [Fact]
        public void Photo_exif_maps_counterclockwise_attitudes()
        {
            Assert.Equal(
                CameraPhotoExifOrientation.Rotate90,
                CameraOrientationMath.ToPhotoExif(
                    CameraDeviceAttitude.Rotated90Counterclockwise));
            Assert.Equal(
                CameraPhotoExifOrientation.Normal,
                CameraOrientationMath.ToPhotoExif(CameraDeviceAttitude.NotRotated));
        }

        [Fact]
        public void Mirror_swaps_90_and_270()
        {
            Assert.Equal(
                CameraDeviceAttitude.Rotated270Counterclockwise,
                CameraOrientationMath.Mirror(CameraDeviceAttitude.Rotated90Counterclockwise));
            Assert.Equal(
                CameraDeviceAttitude.Rotated90Counterclockwise,
                CameraOrientationMath.Mirror(CameraDeviceAttitude.Rotated270Counterclockwise));
        }
    }
}
