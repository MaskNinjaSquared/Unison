using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.ViewModels;
using Windows.ApplicationModel;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Views
{
    /// <summary>
    /// In-chat camera overlay. Owns <see cref="MediaCapture"/> / <see cref="CaptureElement"/>;
    /// chrome and photo bytes go through <see cref="CameraPickViewModel"/>.
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
        private static readonly object ClosedDataContext = new object();

        private CameraPickViewModel _viewModel;
        private MediaCapture _mediaCapture;
        private IReadOnlyList<DeviceInformation> _cameras;
        private int _cameraIndex;
        private bool _isStarting;
        private bool _lifecycleHooked;
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

            var capture = new MediaCapture();
            var settings = new MediaCaptureInitializationSettings
            {
                VideoDeviceId = deviceId,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                PhotoCaptureSource = PhotoCaptureSource.VideoPreview
            };

            await capture.InitializeAsync(settings);
            _mediaCapture = capture;
            PreviewElement.Source = capture;
            await capture.StartPreviewAsync();
            UpdatePreviewAspectFromCapture();
            LayoutPreviewHost();
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
        /// Fits the preview grid inside the available area while keeping the capture aspect
        /// (Windows Camera letterbox behaviour).
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

        private async Task StopPreviewAsync()
        {
            PreviewElement.Source = null;
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
