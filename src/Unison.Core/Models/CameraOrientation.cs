// =============================================================================
// Camera orientation domain (no WinRT).
//
// Maps the Microsoft CameraStarterKit / Unigram CameraRotationHelper vocabulary
// so MediaCapture preview + JPEG EXIF can be corrected on portrait-first phones.
// =============================================================================

namespace Unison.Core.Models
{
    /// <summary>Where the camera sits on the device enclosure.</summary>
    public enum CameraEnclosurePanel
    {
        Unknown = 0,
        Front = 1,
        Back = 2,
    }

    /// <summary>
    /// Display / page orientation as reported to the orientation math
    /// (same cases as WinRT <c>DisplayOrientations</c>).
    /// </summary>
    public enum CameraDisplayOrientation
    {
        Landscape = 0,
        Portrait = 1,
        LandscapeFlipped = 2,
        PortraitFlipped = 3,
    }

    /// <summary>
    /// Device attitude relative to landscape-native reading
    /// (same cases as WinRT <c>SimpleOrientation</c> used by the camera sample).
    /// </summary>
    public enum CameraDeviceAttitude
    {
        NotRotated = 0,
        Rotated90Counterclockwise = 1,
        Rotated180Counterclockwise = 2,
        Rotated270Counterclockwise = 3,
        FaceUp = 4,
        FaceDown = 5,
    }

    /// <summary>
    /// Windows <c>PhotoOrientation</c> / <c>System.Photo.Orientation</c> values
    /// written into JPEG EXIF when saving a still.
    /// </summary>
    public enum CameraPhotoExifOrientation : ushort
    {
        Normal = 1,
        Rotate180 = 3,
        Rotate90 = 6,
        Rotate270 = 8,
    }

    /// <summary>Enclosure facts needed to open an orientation session.</summary>
    public sealed class CameraEnclosureInfo
    {
        public CameraEnclosureInfo(
            CameraEnclosurePanel panel,
            int rotationAngleInDegreesClockwise)
        {
            Panel = panel;
            RotationAngleInDegreesClockwise = NormalizeDegrees(rotationAngleInDegreesClockwise);
        }

        public CameraEnclosurePanel Panel { get; }

        public int RotationAngleInDegreesClockwise { get; }

        /// <summary>
        /// True when the camera is not fixed on the device (USB webcam / unknown panel).
        /// </summary>
        public bool IsExternal => Panel == CameraEnclosurePanel.Unknown;

        private static int NormalizeDegrees(int degrees)
        {
            int n = degrees % 360;
            if (n < 0)
            {
                n += 360;
            }

            return n;
        }
    }
}
