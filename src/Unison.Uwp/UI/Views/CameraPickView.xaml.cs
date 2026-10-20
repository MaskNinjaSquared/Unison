using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Windows.ApplicationModel;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Views
{
    /// <summary>
    /// In-chat camera overlay. Owns <see cref="MediaCapture"/> / <see cref="CaptureElement"/>;
    /// chrome and photo bytes go through <see cref="CameraPickViewModel"/>.
    /// Preview / still orientation go through <see cref="ICameraOrientationService"/>.
    /// </summary>
    /// <remarks>
    /// WinRT <c>IAsyncAction</c> / <c>IAsyncOperation</c> are awaited without
    /// <c>ConfigureAwait</c> — that extension is for <see cref="Task"/> only (same as
    /// <see cref="Services.AudioRecordingService"/>).
    /// Suspend / minimize / hide notify <see cref="CameraPickViewModel.Cancel"/> so a late
    /// shutter never opens the image-send path.
    /// </remarks>
    public sealed partial class CameraPickView : UserControl
    {
        // MF_MT_VIDEO_ROTATION — same GUID as CameraStarterKit / Unigram.
        private static readonly Guid PreviewRotationKey =
            new Guid("C380465D-2271-428C-9B83-ECEA3B4A85C1");

        private static readonly object ClosedDataContext = new object();

        private CameraPickViewModel _viewModel;
        private MediaCapture _mediaCapture;
        private ICameraOrientationService _orientationService;
        private ICameraOrientationSession _orientationSession;
        private IReadOnlyList<DeviceInformation> _cameras;
        private int _cameraIndex;
        private bool _isStarting;
        private bool _lifecycleHooked;
        private bool? _isWindowsMobile;
        private double _previewAspect = 3.0 / 4.0;

        public CameraPickView()
        {
            InitializeComponent();
            DataContext = ClosedDataContext;
            Loaded += CameraPickView_Loaded;
            Unloaded += CameraPickView_Unloaded;
        }

        public CameraPickViewModel ViewModel
        {
            get => _viewModel;
            set
            {
                if (ReferenceEquals(_viewModel, value))
                {
                    return;
                }

                DetachViewModel();
                _viewModel = value;
                DataContext = (object)value ?? ClosedDataContext;
                Bindings.Update();

                if (_viewModel != null)
                {
                    AttachViewModel();
                    AttachLifecycleWatch();
                    _ = StartPreviewAsync();
                }
                else
                {
                    _ = StopPreviewAsync();
                }
            }
        }

        public event EventHandler CloseRequested;

        public event EventHandler<CameraPhotoCapturedEventArgs> PhotoCaptured;

        private void AttachViewModel()
        {
            _viewModel.CloseRequested += ViewModel_CloseRequested;
            _viewModel.CaptureRequested += ViewModel_CaptureRequested;
            _viewModel.SwitchCameraRequested += ViewModel_SwitchCameraRequested;
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            _viewModel.PhotoCaptured += ViewModel_PhotoCaptured;
        }

        private void DetachViewModel()
        {
            if (_viewModel == null)
            {
                return;
            }

            _viewModel.CloseRequested -= ViewModel_CloseRequested;
            _viewModel.CaptureRequested -= ViewModel_CaptureRequested;
            _viewModel.SwitchCameraRequested -= ViewModel_SwitchCameraRequested;
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _viewModel.PhotoCaptured -= ViewModel_PhotoCaptured;
        }

        private void CameraPickView_Loaded(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null)
            {
                AttachLifecycleWatch();
            }
        }

        private void CameraPickView_Unloaded(object sender, RoutedEventArgs e)
        {
            DetachLifecycleWatch();
            ViewModel = null;
        }

        private void AttachLifecycleWatch()
        {
            if (_lifecycleHooked)
            {
                return;
            }

            Application.Current.EnteredBackground += App_EnteredBackground;
            Application.Current.Suspending += App_Suspending;
            CoreWindow core = Window.Current?.CoreWindow;
            if (core != null)
            {
                core.VisibilityChanged += CoreWindow_VisibilityChanged;
            }

            _lifecycleHooked = true;
        }

        private void DetachLifecycleWatch()
        {
            if (!_lifecycleHooked)
            {
                return;
            }

            Application.Current.EnteredBackground -= App_EnteredBackground;
            Application.Current.Suspending -= App_Suspending;
            CoreWindow core = Window.Current?.CoreWindow;
            if (core != null)
            {
                core.VisibilityChanged -= CoreWindow_VisibilityChanged;
            }

            _lifecycleHooked = false;
        }

        private void App_EnteredBackground(object sender, EnteredBackgroundEventArgs e)
        {
            CancelForBackground("entered-background");
        }

        private void App_Suspending(object sender, SuspendingEventArgs e)
        {
            CancelForBackground("suspending");
        }

        private void CoreWindow_VisibilityChanged(CoreWindow sender, VisibilityChangedEventArgs args)
        {
            if (!args.Visible)
            {
                CancelForBackground("window-hidden");
            }
        }

        private void CancelForBackground(string reason)
        {
            CameraPickViewModel vm = _viewModel;
            if (vm == null || vm.IsCancelled)
            {
                return;
            }

            System.Diagnostics.Debug.WriteLine("[CameraPickView] Cancel: " + reason);
            vm.Cancel();
        }

        private void ViewModel_CloseRequested(object sender, EventArgs e)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        private void ViewModel_PhotoCaptured(object sender, CameraPhotoCapturedEventArgs e)
        {
            PhotoCaptured?.Invoke(this, e);
        }

        private async void ViewModel_CaptureRequested(object sender, EventArgs e)
        {
            await CapturePhotoAsync();
        }

        private async void ViewModel_SwitchCameraRequested(object sender, EventArgs e)
        {
            await SwitchCameraAsync();
        }

        private void ViewModel_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CameraPickViewModel.IsFlashOn))
            {
                ApplyFlash();
            }
        }

        private async Task StartPreviewAsync()
        {
            CameraPickViewModel vm = _viewModel;
            if (vm == null || _isStarting)
            {
                return;
            }

            _isStarting = true;
            vm.IsBusy = true;
            vm.IsPreviewReady = false;
            try
            {
                await EnsureCameraListAsync();
                if (_cameras == null || _cameras.Count == 0)
                {
                    vm.ReportDevices(0, false);
                    return;
                }

                if (_cameraIndex < 0 || _cameraIndex >= _cameras.Count)
                {
                    _cameraIndex = 0;
                }

                await InitializeCaptureAsync(_cameras[_cameraIndex].Id);
                // Closed while starting (Unloaded / ViewModel = null) — skip chrome updates.
                if (!ReferenceEquals(_viewModel, vm))
                {
                    return;
                }

                vm.ReportDevices(
                    _cameras.Count,
                    _mediaCapture != null &&
                    _mediaCapture.VideoDeviceController.FlashControl.Supported);
                ApplyFlash();
                vm.IsPreviewReady = _mediaCapture != null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[CameraPickView] StartPreview: " + ex.Message);
                if (ReferenceEquals(_viewModel, vm))
                {
                    vm.IsPreviewReady = false;
                }
            }
            finally
            {
                if (ReferenceEquals(_viewModel, vm))
                {
                    vm.IsBusy = false;
                }

                _isStarting = false;
            }
        }

        private async Task EnsureCameraListAsync()
        {
            if (_cameras != null)
            {
                return;
            }

            DeviceInformationCollection devices =
                await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            var list = new List<DeviceInformation>(devices.Count);
            for (int i = 0; i < devices.Count; i++)
            {
                list.Add(devices[i]);
            }

            _cameras = list;
            _cameraIndex = 0;
        }

        private async Task InitializeCaptureAsync(string deviceId)
        {
            await StopPreviewAsync();

            DeviceInformation device = FindCamera(deviceId);
            CameraEnclosureInfo enclosure = MapEnclosure(device);
            ICameraOrientationService orientation = ResolveOrientationService();
            ICameraOrientationSession session = orientation?.Bind(enclosure);

            var capture = new MediaCapture();
            try
            {
                var settings = new MediaCaptureInitializationSettings
                {
                    VideoDeviceId = deviceId,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                    PhotoCaptureSource = PhotoCaptureSource.VideoPreview
                };

                await capture.InitializeAsync(settings);
                _mediaCapture = capture;
                _orientationSession = session;
                session = null;
                if (_orientationSession != null)
                {
                    _orientationSession.Changed += OrientationSession_Changed;
                }

                PreviewElement.Source = capture;
                PreviewElement.FlowDirection =
                    _orientationSession != null && _orientationSession.ShouldMirrorPreview
                        ? FlowDirection.RightToLeft
                        : FlowDirection.LeftToRight;
                await capture.StartPreviewAsync();
                await SetPreviewRotationAsync();
                UpdateChromeOrientation();
                UpdatePreviewAspectFromCapture();
                LayoutPreviewHost();
            }
            catch
            {
                if (session != null)
                {
                    session.Dispose();
                }

                if (ReferenceEquals(_mediaCapture, capture))
                {
                    _mediaCapture = null;
                    DisposeOrientationSession();
                }

                capture.Dispose();
                throw;
            }
        }

        private DeviceInformation FindCamera(string deviceId)
        {
            if (_cameras == null || string.IsNullOrEmpty(deviceId))
            {
                return null;
            }

            for (int i = 0; i < _cameras.Count; i++)
            {
                if (string.Equals(_cameras[i].Id, deviceId, StringComparison.Ordinal))
                {
                    return _cameras[i];
                }
            }

            return null;
        }

        private static CameraEnclosureInfo MapEnclosure(DeviceInformation device)
        {
            if (device?.EnclosureLocation == null)
            {
                return new CameraEnclosureInfo(CameraEnclosurePanel.Unknown, 0);
            }

            Windows.Devices.Enumeration.EnclosureLocation location = device.EnclosureLocation;
            CameraEnclosurePanel panel;
            switch (location.Panel)
            {
                case Windows.Devices.Enumeration.Panel.Front:
                    panel = CameraEnclosurePanel.Front;
                    break;
                case Windows.Devices.Enumeration.Panel.Back:
                    panel = CameraEnclosurePanel.Back;
                    break;
                default:
                    panel = CameraEnclosurePanel.Unknown;
                    break;
            }

            return new CameraEnclosureInfo(
                panel,
                (int)location.RotationAngleInDegreesClockwise);
        }

        private ICameraOrientationService ResolveOrientationService()
        {
            if (_orientationService != null)
            {
                return _orientationService;
            }

            try
            {
                _orientationService = App.Services?.GetService<ICameraOrientationService>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[CameraPickView] Orientation service: " + ex.Message);
            }

            return _orientationService;
        }

        private async void OrientationSession_Changed(object sender, CameraOrientationChangedEventArgs e)
        {
            try
            {
                if (e != null && e.UpdatePreview)
                {
                    await SetPreviewRotationAsync();
                }

                await Dispatcher.RunAsync(
                    CoreDispatcherPriority.Normal,
                    UpdateChromeOrientation);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[CameraPickView] OrientationChanged: " + ex.Message);
            }
        }

        private async Task SetPreviewRotationAsync()
        {
            MediaCapture capture = _mediaCapture;
            ICameraOrientationSession session = _orientationSession;
            if (capture == null || session == null || session.IsExternalCamera)
            {
                return;
            }

            try
            {
                int degrees = session.GetPreviewRotationClockwiseDegrees();
                var props = capture.VideoDeviceController.GetMediaStreamProperties(
                    MediaStreamType.VideoPreview);
                props.Properties[PreviewRotationKey] = degrees;
                await capture.SetEncodingPropertiesAsync(
                    MediaStreamType.VideoPreview,
                    props,
                    null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[CameraPickView] SetPreviewRotation: " + ex.Message);
            }
        }

        private void UpdateChromeOrientation()
        {
            ICameraOrientationSession session = _orientationSession;
            double angle = session == null
                ? 0
                : session.GetUiRotationClockwiseDegrees();
            var transform = new RotateTransform { Angle = angle };
            if (CaptureButton != null)
            {
                CaptureButton.RenderTransform = transform;
                CaptureButton.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            }

            if (BackButton != null)
            {
                BackButton.RenderTransform = transform;
                BackButton.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            }
        }

        private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            LayoutPreviewHost();
        }

        private void UpdatePreviewAspectFromCapture()
        {
            if (_mediaCapture == null)
            {
                return;
            }

            try
            {
                var props = _mediaCapture.VideoDeviceController.GetMediaStreamProperties(
                    MediaStreamType.VideoPreview) as VideoEncodingProperties;
                if (props != null && props.Width > 0 && props.Height > 0)
                {
                    _previewAspect = (double)props.Width / props.Height;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[CameraPickView] Aspect: " + ex.Message);
            }
        }

        /// <summary>
        /// Desktop: letterbox the preview to the capture aspect.
        /// Mobile: full-bleed host so <see cref="CaptureElement"/> UniformToFill crops like
        /// the Windows Camera app; the JPEG is cropped to the same viewport after capture.
        /// </summary>
        private void LayoutPreviewHost()
        {
            if (PreviewHost == null || RootGrid == null)
            {
                return;
            }

            double availableWidth = RootGrid.ActualWidth;
            double availableHeight = RootGrid.ActualHeight;
            if (availableWidth <= 0 || availableHeight <= 0 || _previewAspect <= 0)
            {
                return;
            }

            if (IsWindowsMobile())
            {
                PreviewHost.Width = availableWidth;
                PreviewHost.Height = availableHeight;
                return;
            }

            double width = availableWidth;
            double height = width / _previewAspect;
            if (height > availableHeight)
            {
                height = availableHeight;
                width = height * _previewAspect;
            }

            PreviewHost.Width = width;
            PreviewHost.Height = height;
        }

        private bool IsWindowsMobile()
        {
            if (_isWindowsMobile.HasValue)
            {
                return _isWindowsMobile.Value;
            }

            try
            {
                _isWindowsMobile = App.Services?.GetService<ISystemInfoProvider>()?.IsMobile() == true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[CameraPickView] IsMobile: " + ex.Message);
                _isWindowsMobile = false;
            }

            return _isWindowsMobile == true;
        }

        private async Task StopPreviewAsync()
        {
            PreviewElement.Source = null;
            DisposeOrientationSession();
            MediaCapture capture = _mediaCapture;
            _mediaCapture = null;
            if (capture == null)
            {
                return;
            }

            try
            {
                await capture.StopPreviewAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[CameraPickView] StopPreview: " + ex.Message);
            }

            capture.Dispose();
        }

        private void DisposeOrientationSession()
        {
            ICameraOrientationSession session = _orientationSession;
            _orientationSession = null;
            if (session == null)
            {
                return;
            }

            session.Changed -= OrientationSession_Changed;
            session.Dispose();
        }

        private async Task SwitchCameraAsync()
        {
            CameraPickViewModel vm = _viewModel;
            if (vm == null || _cameras == null || _cameras.Count < 2)
            {
                return;
            }

            vm.IsBusy = true;
            vm.IsPreviewReady = false;
            try
            {
                _cameraIndex = (_cameraIndex + 1) % _cameras.Count;
                await InitializeCaptureAsync(_cameras[_cameraIndex].Id);
                if (!ReferenceEquals(_viewModel, vm))
                {
                    return;
                }

                vm.ReportDevices(
                    _cameras.Count,
                    _mediaCapture != null &&
                    _mediaCapture.VideoDeviceController.FlashControl.Supported);
                ApplyFlash();
                vm.IsPreviewReady = _mediaCapture != null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[CameraPickView] SwitchCamera: " + ex.Message);
            }
            finally
            {
                if (ReferenceEquals(_viewModel, vm))
                {
                    vm.IsBusy = false;
                }
            }
        }

        private void ApplyFlash()
        {
            if (_mediaCapture == null || _viewModel == null)
            {
                return;
            }

            try
            {
                var flash = _mediaCapture.VideoDeviceController.FlashControl;
                if (!flash.Supported)
                {
                    _viewModel.ReportFlashApplied(false);
                    return;
                }

                try
                {
                    if (flash.Auto)
                    {
                        flash.Auto = false;
                    }
                }
                catch (Exception)
                {
                    // Some devices expose Auto as read-only.
                }

                flash.Enabled = _viewModel.IsFlashOn;
                _viewModel.ReportFlashApplied(flash.Enabled);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[CameraPickView] ApplyFlash: " + ex.Message);
            }
        }

        private async Task CapturePhotoAsync()
        {
            // Hold a local ref: ReportPhoto → PhotoCaptured closes the overlay and sets
            // ViewModel = null before this method's finally runs.
            CameraPickViewModel vm = _viewModel;
            MediaCapture capture = _mediaCapture;
            if (capture == null || vm == null)
            {
                return;
            }

            vm.IsBusy = true;
            try
            {
                if (vm.IsCancelled)
                {
                    return;
                }

                ApplyFlash();
                double viewportWidth = PreviewHost?.ActualWidth ?? 0;
                double viewportHeight = PreviewHost?.ActualHeight ?? 0;
                using (var stream = new InMemoryRandomAccessStream())
                {
                    await capture.CapturePhotoToStreamAsync(
                        ImageEncodingProperties.CreateJpeg(),
                        stream);

                    if (vm.IsCancelled)
                    {
                        return;
                    }

                    stream.Seek(0);
                    byte[] bytes = await ReadStreamBytesAsync(stream);
                    // PhotoCaptureSource.VideoPreview already encodes with the preview
                    // stream rotation. Do NOT stamp sensor-based EXIF on top — that was
                    // flipping Mobile portrait stills 180° vs the live preview.
                    int previewRotation = _orientationSession != null
                        ? _orientationSession.GetPreviewRotationClockwiseDegrees()
                        : 0;
                    bytes = await CropJpegToPreviewViewportAsync(
                        bytes,
                        viewportWidth,
                        viewportHeight,
                        previewRotation);
                    if (vm.IsCancelled)
                    {
                        return;
                    }

                    vm.ReportPhoto(bytes);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[CameraPickView] Capture: " + ex.Message);
            }
            finally
            {
                // Only clear busy if this VM is still bound (close already tore it down).
                if (ReferenceEquals(_viewModel, vm))
                {
                    vm.IsBusy = false;
                }
            }
        }

        /// <summary>
        /// Re-encodes the JPEG to the same center crop UniformToFill shows in
        /// <see cref="PreviewHost"/> (1:1 with the live preview framing).
        /// </summary>
        private static async Task<byte[]> CropJpegToPreviewViewportAsync(
            byte[] jpegBytes,
            double viewportWidth,
            double viewportHeight,
            int previewRotationClockwiseDegrees)
        {
            if (jpegBytes == null || jpegBytes.Length == 0 ||
                viewportWidth <= 0 || viewportHeight <= 0)
            {
                return jpegBytes;
            }

            try
            {
                using (var input = new InMemoryRandomAccessStream())
                {
                    await input.WriteAsync(jpegBytes.AsBuffer());
                    input.Seek(0);

                    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);
                    CameraPhotoExifOrientation jpegExif = await ReadJpegExifOrientationAsync(decoder);
                    bool bakePreview = CameraOrientationMath.ShouldBakePreviewRotation(
                        previewRotationClockwiseDegrees,
                        jpegExif,
                        (int)decoder.PixelWidth,
                        (int)decoder.PixelHeight);
                    int bakeDegrees = bakePreview
                        ? CameraOrientationMath.NormalizeClockwiseDegrees(
                            previewRotationClockwiseDegrees)
                        : 0;
                    BitmapRotation bakeRotation = ToBitmapRotation(bakeDegrees);

                    int sourceWidth;
                    int sourceHeight;
                    ExifOrientationMode exifMode;
                    if (bakePreview)
                    {
                        // No usable EXIF — rotate like the live preview, then crop.
                        exifMode = ExifOrientationMode.IgnoreExifOrientation;
                        if (bakeDegrees == 90 || bakeDegrees == 270)
                        {
                            sourceWidth = (int)decoder.PixelHeight;
                            sourceHeight = (int)decoder.PixelWidth;
                        }
                        else
                        {
                            sourceWidth = (int)decoder.PixelWidth;
                            sourceHeight = (int)decoder.PixelHeight;
                        }
                    }
                    else
                    {
                        exifMode = ExifOrientationMode.RespectExifOrientation;
                        sourceWidth = (int)decoder.OrientedPixelWidth;
                        sourceHeight = (int)decoder.OrientedPixelHeight;
                    }

                    CameraPreviewCropMath.PixelRect crop =
                        CameraPreviewCropMath.ComputeUniformToFillCrop(
                            sourceWidth,
                            sourceHeight,
                            viewportWidth,
                            viewportHeight);

                    bool cropIsFull = crop.Width > 0 &&
                        crop.Height > 0 &&
                        crop.CoversSource(sourceWidth, sourceHeight);
                    if ((crop.Width <= 0 || crop.Height <= 0 || cropIsFull) && !bakePreview)
                    {
                        return jpegBytes;
                    }

                    var transform = new BitmapTransform
                    {
                        Rotation = bakeRotation
                    };
                    if (crop.Width > 0 && crop.Height > 0 && !cropIsFull)
                    {
                        transform.Bounds = new BitmapBounds
                        {
                            X = (uint)crop.X,
                            Y = (uint)crop.Y,
                            Width = (uint)crop.Width,
                            Height = (uint)crop.Height
                        };
                    }
                    else
                    {
                        crop = new CameraPreviewCropMath.PixelRect(
                            0,
                            0,
                            sourceWidth,
                            sourceHeight);
                    }

                    PixelDataProvider pixels = await decoder.GetPixelDataAsync(
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Ignore,
                        transform,
                        exifMode,
                        ColorManagementMode.DoNotColorManage);

                    using (var output = new InMemoryRandomAccessStream())
                    {
                        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(
                            BitmapEncoder.JpegEncoderId,
                            output);
                        encoder.SetPixelData(
                            BitmapPixelFormat.Bgra8,
                            BitmapAlphaMode.Ignore,
                            (uint)crop.Width,
                            (uint)crop.Height,
                            decoder.DpiX,
                            decoder.DpiY,
                            pixels.DetachPixelData());
                        await encoder.FlushAsync();
                        output.Seek(0);
                        return await ReadStreamBytesAsync(output);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[CameraPickView] CropJpeg: " + ex.Message);
                return jpegBytes;
            }
        }

        private static async Task<CameraPhotoExifOrientation> ReadJpegExifOrientationAsync(
            BitmapDecoder decoder)
        {
            try
            {
                var names = new[] { "System.Photo.Orientation" };
                BitmapPropertySet props =
                    await decoder.BitmapProperties.GetPropertiesAsync(names);
                if (props != null &&
                    props.ContainsKey("System.Photo.Orientation") &&
                    props["System.Photo.Orientation"].Value is ushort value)
                {
                    return (CameraPhotoExifOrientation)value;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[CameraPickView] ReadExif: " + ex.Message);
            }

            return CameraPhotoExifOrientation.Normal;
        }

        private static BitmapRotation ToBitmapRotation(int clockwiseDegrees)
        {
            switch (CameraOrientationMath.NormalizeClockwiseDegrees(clockwiseDegrees))
            {
                case 90:
                    return BitmapRotation.Clockwise90Degrees;
                case 180:
                    return BitmapRotation.Clockwise180Degrees;
                case 270:
                    return BitmapRotation.Clockwise270Degrees;
                default:
                    return BitmapRotation.None;
            }
        }

        private static async Task<byte[]> ReadStreamBytesAsync(IRandomAccessStream stream)
        {
            using (var reader = new DataReader(stream.GetInputStreamAt(0)))
            {
                uint size = (uint)stream.Size;
                await reader.LoadAsync(size);
                byte[] bytes = new byte[size];
                reader.ReadBytes(bytes);
                return bytes;
            }
        }
    }
}
