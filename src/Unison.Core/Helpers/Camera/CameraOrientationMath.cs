// =============================================================================
// CameraOrientationMath
//
// Pure orientation math from Microsoft CameraStarterKit / Unigram
// CameraRotationHelper — no WinRT. Host supplies current display + device
// attitude; this returns preview / capture / UI clockwise degrees and EXIF.
// =============================================================================

using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class CameraOrientationMath
    {
        public static bool ShouldMirrorPreview(CameraEnclosureInfo enclosure)
        {
            return enclosure != null && enclosure.Panel == CameraEnclosurePanel.Front;
        }

        /// <summary>Clockwise degrees for MF_MT_VIDEO_ROTATION on the preview stream.</summary>
        public static int GetPreviewRotationClockwiseDegrees(
            CameraEnclosureInfo enclosure,
            CameraDisplayOrientation currentDisplay,
            CameraDisplayOrientation nativeDisplay)
        {
            if (enclosure == null || enclosure.IsExternal)
            {
                return 0;
            }

            CameraDeviceAttitude result = FromDisplay(currentDisplay, nativeDisplay);
            result = Subtract(result, CameraRelativeToNative(enclosure, nativeDisplay));
            if (ShouldMirrorPreview(enclosure))
            {
                result = Mirror(result);
            }

            return ToClockwiseDegrees(result);
        }

        /// <summary>Clockwise degrees for JPEG / video capture metadata.</summary>
        public static int GetCaptureRotationClockwiseDegrees(
            CameraEnclosureInfo enclosure,
            CameraDeviceAttitude deviceAttitude,
            CameraDisplayOrientation nativeDisplay)
        {
            if (enclosure == null || enclosure.IsExternal)
            {
                return 0;
            }

            CameraDeviceAttitude attitude = NormalizeAttitude(deviceAttitude);
            CameraDeviceAttitude result = Subtract(
                attitude,
                CameraRelativeToNative(enclosure, nativeDisplay));
            if (ShouldMirrorPreview(enclosure))
            {
                result = Mirror(result);
            }

            return ToClockwiseDegrees(result);
        }

        public static CameraPhotoExifOrientation GetCapturePhotoExif(
            CameraEnclosureInfo enclosure,
            CameraDeviceAttitude deviceAttitude,
            CameraDisplayOrientation nativeDisplay)
        {
            return ToPhotoExif(FromClockwiseDegrees(
                GetCaptureRotationClockwiseDegrees(enclosure, deviceAttitude, nativeDisplay)));
        }

        /// <summary>
        /// Clockwise degrees to rotate chrome so buttons stay upright relative to the user.
        /// </summary>
        public static int GetUiRotationClockwiseDegrees(
            CameraEnclosureInfo enclosure,
            CameraDeviceAttitude deviceAttitude,
            CameraDisplayOrientation currentDisplay,
            CameraDisplayOrientation nativeDisplay)
        {
            if (enclosure == null || enclosure.IsExternal)
            {
                return 0;
            }

            CameraDeviceAttitude attitude = NormalizeAttitude(deviceAttitude);
            CameraDeviceAttitude display = FromDisplay(currentDisplay, nativeDisplay);
            return ToClockwiseDegrees(Subtract(display, attitude));
        }

        public static int ToClockwiseDegrees(CameraDeviceAttitude attitude)
        {
            switch (NormalizeAttitude(attitude))
            {
                case CameraDeviceAttitude.Rotated90Counterclockwise:
                    return 270;
                case CameraDeviceAttitude.Rotated180Counterclockwise:
                    return 180;
                case CameraDeviceAttitude.Rotated270Counterclockwise:
                    return 90;
                default:
                    return 0;
            }
        }

        public static CameraDeviceAttitude FromClockwiseDegrees(int degrees)
        {
            int n = degrees % 360;
            if (n < 0)
            {
                n += 360;
            }

            switch (n)
            {
                case 270:
                    return CameraDeviceAttitude.Rotated90Counterclockwise;
                case 180:
                    return CameraDeviceAttitude.Rotated180Counterclockwise;
                case 90:
                    return CameraDeviceAttitude.Rotated270Counterclockwise;
                default:
                    return CameraDeviceAttitude.NotRotated;
            }
        }

        public static CameraPhotoExifOrientation ToPhotoExif(CameraDeviceAttitude attitude)
        {
            switch (NormalizeAttitude(attitude))
            {
                case CameraDeviceAttitude.Rotated90Counterclockwise:
                    return CameraPhotoExifOrientation.Rotate90;
                case CameraDeviceAttitude.Rotated180Counterclockwise:
                    return CameraPhotoExifOrientation.Rotate180;
                case CameraDeviceAttitude.Rotated270Counterclockwise:
                    return CameraPhotoExifOrientation.Rotate270;
                default:
                    return CameraPhotoExifOrientation.Normal;
            }
        }

        /// <summary>
        /// True when the JPEG still needs the live preview's MF_MT_VIDEO_ROTATION baked in.
        /// </summary>
        /// <remarks>
        /// <c>PhotoCaptureSource.VideoPreview</c> may already encode an upright buffer (EXIF
        /// Normal + portrait pixels). Re-applying preview degrees — or stamping sensor EXIF —
        /// flips Mobile portrait stills ~180° vs the viewfinder. Only bake when EXIF is Normal
        /// and a 90°/270° preview rotation still sees a landscape pixel buffer.
        /// </remarks>
        public static bool ShouldBakePreviewRotation(
            int previewRotationClockwiseDegrees,
            CameraPhotoExifOrientation jpegExif,
            int pixelWidth,
            int pixelHeight)
        {
            if (jpegExif != CameraPhotoExifOrientation.Normal)
            {
                return false;
            }

            int degrees = NormalizeClockwiseDegrees(previewRotationClockwiseDegrees);
            if (degrees == 0)
            {
                return false;
            }

            if (degrees == 90 || degrees == 270)
            {
                // Preview is on its side relative to the sensor — bake only while the JPEG
                // is still landscape (not already upright from the preview encoder).
                return pixelWidth > 0 && pixelHeight > 0 && pixelWidth > pixelHeight;
            }

            // 180°: aspect cannot tell; bake when EXIF did not already express it.
            return degrees == 180;
        }

        /// <summary>Maps clockwise degrees to a 90° step (0 / 90 / 180 / 270).</summary>
        public static int NormalizeClockwiseDegrees(int degrees)
        {
            int n = degrees % 360;
            if (n < 0)
            {
                n += 360;
            }

            // Snap to nearest 90° for BitmapRotation / MF.
            int step = ((n + 45) / 90) * 90;
            return step == 360 ? 0 : step;
        }

        public static CameraDeviceAttitude FromDisplay(
            CameraDisplayOrientation current,
            CameraDisplayOrientation native)
        {
            CameraDeviceAttitude result;
            switch (current)
            {
                case CameraDisplayOrientation.Landscape:
                    result = CameraDeviceAttitude.NotRotated;
                    break;
                case CameraDisplayOrientation.PortraitFlipped:
                    result = CameraDeviceAttitude.Rotated90Counterclockwise;
                    break;
                case CameraDisplayOrientation.LandscapeFlipped:
                    result = CameraDeviceAttitude.Rotated180Counterclockwise;
                    break;
                case CameraDisplayOrientation.Portrait:
                default:
                    result = CameraDeviceAttitude.Rotated270Counterclockwise;
                    break;
            }

            if (native == CameraDisplayOrientation.Portrait)
            {
                result = Add(result, CameraDeviceAttitude.Rotated90Counterclockwise);
            }

            return result;
        }

        public static CameraDeviceAttitude CameraRelativeToNative(
            CameraEnclosureInfo enclosure,
            CameraDisplayOrientation nativeDisplay)
        {
            CameraDeviceAttitude enclosureAngle = FromClockwiseDegrees(
                enclosure?.RotationAngleInDegreesClockwise ?? 0);

            if (nativeDisplay == CameraDisplayOrientation.Portrait &&
                enclosure != null &&
                !enclosure.IsExternal)
            {
                enclosureAngle = Add(
                    CameraDeviceAttitude.Rotated90Counterclockwise,
                    enclosureAngle);
            }

            return enclosureAngle;
        }

        public static CameraDeviceAttitude Add(CameraDeviceAttitude a, CameraDeviceAttitude b)
        {
            return FromClockwiseDegrees(ToClockwiseDegrees(a) + ToClockwiseDegrees(b));
        }

        public static CameraDeviceAttitude Subtract(CameraDeviceAttitude a, CameraDeviceAttitude b)
        {
            return FromClockwiseDegrees(360 + ToClockwiseDegrees(a) - ToClockwiseDegrees(b));
        }

        public static CameraDeviceAttitude Mirror(CameraDeviceAttitude attitude)
        {
            switch (NormalizeAttitude(attitude))
            {
                case CameraDeviceAttitude.Rotated90Counterclockwise:
                    return CameraDeviceAttitude.Rotated270Counterclockwise;
                case CameraDeviceAttitude.Rotated270Counterclockwise:
                    return CameraDeviceAttitude.Rotated90Counterclockwise;
                default:
                    return NormalizeAttitude(attitude);
            }
        }

        private static CameraDeviceAttitude NormalizeAttitude(CameraDeviceAttitude attitude)
        {
            if (attitude == CameraDeviceAttitude.FaceUp ||
                attitude == CameraDeviceAttitude.FaceDown)
            {
                return CameraDeviceAttitude.NotRotated;
            }

            return attitude;
        }
    }
}
