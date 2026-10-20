using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Windows.Devices.Sensors;
using Windows.Graphics.Display;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Unison.Uwp.Services
{
    /// <summary>
    /// WinRT adapter for <see cref="ICameraOrientationService"/> — DisplayInformation +
    /// SimpleOrientationSensor + JPEG EXIF re-encode (Unigram / CameraStarterKit).
    /// </summary>
    public sealed class CameraOrientationService : ICameraOrientationService
    {
        public ICameraOrientationSession Bind(CameraEnclosureInfo enclosure)
        {
            if (enclosure == null)
            {
                throw new ArgumentNullException(nameof(enclosure));
            }

            return new CameraOrientationSession(enclosure);
        }

        public async Task<byte[]> ApplyPhotoExifAsync(
            byte[] jpegBytes,
            CameraPhotoExifOrientation orientation)
        {
            if (jpegBytes == null || jpegBytes.Length == 0)
            {
                return jpegBytes ?? Array.Empty<byte>();
            }

            if (orientation == CameraPhotoExifOrientation.Normal)
            {
                return jpegBytes;
            }

            using (var input = new InMemoryRandomAccessStream())
            {
                await input.WriteAsync(jpegBytes.AsBuffer());
                input.Seek(0);

                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);
                using (var output = new InMemoryRandomAccessStream())
                {
                    BitmapEncoder encoder = await BitmapEncoder.CreateForTranscodingAsync(
                        output,
                        decoder);
                    var properties = new BitmapPropertySet
                    {
                        {
                            "System.Photo.Orientation",
                            new BitmapTypedValue((ushort)orientation, Windows.Foundation.PropertyType.UInt16)
                        }
                    };
                    await encoder.BitmapProperties.SetPropertiesAsync(properties);
                    await encoder.FlushAsync();

                    output.Seek(0);
                    var reader = new DataReader(output.GetInputStreamAt(0));
                    await reader.LoadAsync((uint)output.Size);
                    byte[] result = new byte[output.Size];
                    reader.ReadBytes(result);
                    return result;
                }
            }
        }

        private sealed class CameraOrientationSession : ICameraOrientationSession
        {
            private readonly CameraEnclosureInfo _enclosure;
            private readonly DisplayInformation _display;
            private readonly SimpleOrientationSensor _sensor;
            private bool _disposed;

            public CameraOrientationSession(CameraEnclosureInfo enclosure)
            {
                _enclosure = enclosure;
                _display = DisplayInformation.GetForCurrentView();
                _sensor = SimpleOrientationSensor.GetDefault();

                _display.OrientationChanged += Display_OrientationChanged;
                if (!_enclosure.IsExternal && _sensor != null)
                {
                    _sensor.OrientationChanged += Sensor_OrientationChanged;
                }
            }

            public event EventHandler<CameraOrientationChangedEventArgs> Changed;

            public bool ShouldMirrorPreview =>
                CameraOrientationMath.ShouldMirrorPreview(_enclosure);

            public bool IsExternalCamera => _enclosure.IsExternal;

            public int GetPreviewRotationClockwiseDegrees()
            {
                return CameraOrientationMath.GetPreviewRotationClockwiseDegrees(
                    _enclosure,
                    MapDisplay(_display.CurrentOrientation),
                    MapDisplay(_display.NativeOrientation));
            }

            public int GetUiRotationClockwiseDegrees()
            {
                return CameraOrientationMath.GetUiRotationClockwiseDegrees(
                    _enclosure,
                    MapAttitude(_sensor?.GetCurrentOrientation() ?? SimpleOrientation.NotRotated),
                    MapDisplay(_display.CurrentOrientation),
                    MapDisplay(_display.NativeOrientation));
            }

            public CameraPhotoExifOrientation GetCapturePhotoExif()
            {
                return CameraOrientationMath.GetCapturePhotoExif(
                    _enclosure,
                    MapAttitude(_sensor?.GetCurrentOrientation() ?? SimpleOrientation.NotRotated),
                    MapDisplay(_display.NativeOrientation));
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _display.OrientationChanged -= Display_OrientationChanged;
                if (_sensor != null)
                {
                    _sensor.OrientationChanged -= Sensor_OrientationChanged;
                }
            }

            private void Display_OrientationChanged(DisplayInformation sender, object args)
            {
                Changed?.Invoke(this, new CameraOrientationChangedEventArgs(updatePreview: true));
            }

            private void Sensor_OrientationChanged(
                SimpleOrientationSensor sender,
                SimpleOrientationSensorOrientationChangedEventArgs args)
            {
                if (args.Orientation == SimpleOrientation.Faceup ||
                    args.Orientation == SimpleOrientation.Facedown)
                {
                    return;
                }

                Changed?.Invoke(this, new CameraOrientationChangedEventArgs(updatePreview: false));
            }

            private static CameraDisplayOrientation MapDisplay(DisplayOrientations orientation)
            {
                switch (orientation)
                {
                    case DisplayOrientations.Landscape:
                        return CameraDisplayOrientation.Landscape;
                    case DisplayOrientations.PortraitFlipped:
                        return CameraDisplayOrientation.PortraitFlipped;
                    case DisplayOrientations.LandscapeFlipped:
                        return CameraDisplayOrientation.LandscapeFlipped;
                    case DisplayOrientations.Portrait:
                    default:
                        return CameraDisplayOrientation.Portrait;
                }
            }

            private static CameraDeviceAttitude MapAttitude(SimpleOrientation orientation)
            {
                switch (orientation)
                {
                    case SimpleOrientation.Rotated90DegreesCounterclockwise:
                        return CameraDeviceAttitude.Rotated90Counterclockwise;
                    case SimpleOrientation.Rotated180DegreesCounterclockwise:
                        return CameraDeviceAttitude.Rotated180Counterclockwise;
                    case SimpleOrientation.Rotated270DegreesCounterclockwise:
                        return CameraDeviceAttitude.Rotated270Counterclockwise;
                    case SimpleOrientation.Faceup:
                        return CameraDeviceAttitude.FaceUp;
                    case SimpleOrientation.Facedown:
                        return CameraDeviceAttitude.FaceDown;
                    default:
                        return CameraDeviceAttitude.NotRotated;
                }
            }
        }
    }
}
