// =============================================================================
// CameraPickViewModel
//
// Chrome state for the in-chat camera overlay. WinRT MediaCapture / CaptureElement
// stay in CameraPickView; this type only holds flash / multi-camera flags, commands,
// and raises PhotoCaptured when the view hands back JPEG bytes.
// =============================================================================
using System;
using System.Windows.Input;
using Unison.Core.Helpers;

namespace Unison.Core.ViewModels
{
    /// <summary>JPEG bytes from a successful shutter press.</summary>
    /// <remarks>
    /// Named apart from WinRT <c>Windows.Media.Capture.PhotoCapturedEventArgs</c> so UWP
    /// views that import both namespaces stay unambiguous.
    /// </remarks>
    public sealed class CameraPhotoCapturedEventArgs : EventArgs
    {
        public CameraPhotoCapturedEventArgs(byte[] bytes)
        {
            Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
        }

        public byte[] Bytes { get; }
    }

    /// <summary>
    /// In-chat camera overlay: back, flash, switch camera, shutter. Capture hardware
    /// lives in the UWP view; call <see cref="ReportDevices"/> after enumeration and
    /// <see cref="ReportPhoto"/> after a successful take.
    /// </summary>
    public sealed class CameraPickViewModel : Observable
    {
        private bool _isMultipleCameras;
        private bool _isFlashOn;
        private bool _isFlashAvailable;
        private bool _isBusy;
        private bool _isPreviewReady;
        private bool _isCancelled;

        public CameraPickViewModel()
        {
            CloseCommand = new RelayCommand(
                () => CloseRequested?.Invoke(this, EventArgs.Empty),
                () => !IsBusy && !IsCancelled);

            CaptureCommand = new RelayCommand(
                () => CaptureRequested?.Invoke(this, EventArgs.Empty),
                () => IsPreviewReady && !IsBusy && !IsCancelled);

            // Buttons stay visible; IsEnabled / CanExecute go false when the device has no flash.
            FlashOnCommand = new RelayCommand(
                () => SetFlash(true),
                () => IsFlashAvailable && !IsBusy && !IsCancelled);

            FlashOffCommand = new RelayCommand(
                () => SetFlash(false),
                () => IsFlashAvailable && !IsBusy && !IsCancelled);

            SwitchCameraCommand = new RelayCommand(
                () => SwitchCameraRequested?.Invoke(this, EventArgs.Empty),
                () => IsMultipleCameras && !IsBusy && !IsCancelled);
        }

        public event EventHandler CloseRequested;

        /// <summary>View should take a still via MediaCapture.</summary>
        public event EventHandler CaptureRequested;

        /// <summary>View should cycle to the next video device.</summary>
        public event EventHandler SwitchCameraRequested;

        /// <summary>JPEG bytes ready for the existing image-send preview dialog.</summary>
        public event EventHandler<CameraPhotoCapturedEventArgs> PhotoCaptured;

        public ICommand CloseCommand { get; }

        public ICommand CaptureCommand { get; }

        public ICommand FlashOnCommand { get; }

        public ICommand FlashOffCommand { get; }

        public ICommand SwitchCameraCommand { get; }

        /// <summary>True when WinRT reports more than one video capture device.</summary>
        public bool IsMultipleCameras
        {
            get => _isMultipleCameras;
            private set
            {
                if (Set(ref _isMultipleCameras, value))
                {
                    (SwitchCameraCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public bool IsFlashOn
        {
            get => _isFlashOn;
            private set
            {
                if (Set(ref _isFlashOn, value))
                {
                    RaiseFlashCommands();
                }
            }
        }

        /// <summary>False when the active camera has no flash control.</summary>
        public bool IsFlashAvailable
        {
            get => _isFlashAvailable;
            private set
            {
                if (Set(ref _isFlashAvailable, value))
                {
                    RaiseFlashCommands();
                }
            }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (Set(ref _isBusy, value))
                {
                    RaiseAllCommands();
                }
            }
        }

        public bool IsPreviewReady
        {
            get => _isPreviewReady;
            set
            {
                if (Set(ref _isPreviewReady, value))
                {
                    (CaptureCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>
        /// True after <see cref="Cancel"/> — suspend / minimize / explicit close.
        /// A late <see cref="ReportPhoto"/> is ignored so the image-send path never opens.
        /// </summary>
        public bool IsCancelled => _isCancelled;

        /// <summary>Called by the view after <c>DeviceInformation.FindAllAsync(VideoCapture)</c>.</summary>
        public void ReportDevices(int cameraCount, bool flashSupported)
        {
            if (IsCancelled)
            {
                return;
            }

            IsMultipleCameras = cameraCount > 1;
            IsFlashAvailable = flashSupported;
            if (!flashSupported)
            {
                IsFlashOn = false;
            }
        }

        /// <summary>Applies the desired flash flag after the view writes it to MediaCapture.</summary>
        public void ReportFlashApplied(bool isOn)
        {
            if (IsCancelled)
            {
                return;
            }

            IsFlashOn = isOn;
        }

        /// <summary>
        /// App went to background or was suspended: drop any in-flight capture and ask the
        /// view to tear the overlay down. Idempotent.
        /// </summary>
        public void Cancel()
        {
            if (_isCancelled)
            {
                return;
            }

            _isCancelled = true;
            IsBusy = false;
            IsPreviewReady = false;
            RaiseAllCommands();
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Hands JPEG bytes to subscribers (chat detail → image send preview).</summary>
        public void ReportPhoto(byte[] jpegBytes)
        {
            if (IsCancelled || jpegBytes == null || jpegBytes.Length == 0)
            {
                return;
            }

            PhotoCaptured?.Invoke(this, new CameraPhotoCapturedEventArgs(jpegBytes));
        }

        private void SetFlash(bool on)
        {
            if (!IsFlashAvailable || IsFlashOn == on)
            {
                return;
            }

            IsFlashOn = on;
        }

        private void RaiseFlashCommands()
        {
            (FlashOnCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (FlashOffCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private void RaiseAllCommands()
        {
            (CloseCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CaptureCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SwitchCameraCommand as RelayCommand)?.RaiseCanExecuteChanged();
            RaiseFlashCommands();
        }
    }
}
