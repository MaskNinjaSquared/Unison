// =============================================================================
// ICameraOrientationService
//
// Corrects MediaCapture preview + still EXIF on portrait-first devices.
// WinRT sensors live in the UWP implementation; Core only sees enclosure facts
// and orientation numbers.
// =============================================================================

using System;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Factory for per-camera orientation sessions (preview rotation + JPEG EXIF).
    /// </summary>
    public interface ICameraOrientationService
    {
        /// <summary>
        /// Opens a session bound to one camera enclosure. Dispose when the preview stops.
        /// </summary>
        ICameraOrientationSession Bind(CameraEnclosureInfo enclosure);

        /// <summary>
        /// Re-encodes JPEG bytes with <c>System.Photo.Orientation</c> so viewers show upright.
        /// </summary>
        Task<byte[]> ApplyPhotoExifAsync(byte[] jpegBytes, CameraPhotoExifOrientation orientation);
    }

    /// <summary>
    /// Live orientation for one open <c>MediaCapture</c> preview.
    /// </summary>
    public interface ICameraOrientationSession : IDisposable
    {
        /// <summary>
        /// Raised when display or device attitude changes.
        /// <see cref="CameraOrientationChangedEventArgs.UpdatePreview"/> is true when
        /// the preview stream rotation metadata should be rewritten.
        /// </summary>
        event EventHandler<CameraOrientationChangedEventArgs> Changed;

        bool ShouldMirrorPreview { get; }

        bool IsExternalCamera { get; }

        /// <summary>Clockwise degrees for MF_MT_VIDEO_ROTATION on VideoPreview.</summary>
        int GetPreviewRotationClockwiseDegrees();

        /// <summary>Clockwise degrees for chrome RotateTransform.</summary>
        int GetUiRotationClockwiseDegrees();

        /// <summary>EXIF orientation to stamp on the next still.</summary>
        CameraPhotoExifOrientation GetCapturePhotoExif();
    }

    public sealed class CameraOrientationChangedEventArgs : EventArgs
    {
        public CameraOrientationChangedEventArgs(bool updatePreview)
        {
            UpdatePreview = updatePreview;
        }

        public bool UpdatePreview { get; }
    }
}
