// =============================================================================
// ShellCameraPickHost
//
// Plugs ICameraPickService.AttachUiCapture: toggles ShellViewModel.IsCameraPickOpen
// (x:Load), mounts CameraPickView, completes with JPEG or null.
// =============================================================================
using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Unison.Core.Contracts;
using Unison.Core.ViewModels;
using Unison.Uwp.UI.Views;

namespace Unison.Uwp.UI.Helpers
{
    /// <summary>One Shell-owned camera capture pipeline for MainView / Legacy ShellView.</summary>
    public sealed class ShellCameraPickHost
    {
        private readonly ShellViewModel _shell;
        private readonly ICameraPickService _camera;
        private readonly INavigator _navigator;
        private readonly Func<ContentPresenter> _getPresenter;

        private CameraPickView _overlay;
        private IDisposable _localBack;
        private TaskCompletionSource<byte[]> _pending;

        public ShellCameraPickHost(
            ShellViewModel shell,
            ICameraPickService camera,
            INavigator navigator,
            Func<ContentPresenter> getPresenter)
        {
            _shell = shell ?? throw new ArgumentNullException(nameof(shell));
            _camera = camera ?? throw new ArgumentNullException(nameof(camera));
            _navigator = navigator;
            _getPresenter = getPresenter ?? throw new ArgumentNullException(nameof(getPresenter));
        }

        public void Attach()
        {
            _camera.AttachUiCapture(CaptureAsync);
        }

        public void Detach()
        {
            _camera.DetachUiCapture();
            Complete(null);
            TeardownOverlay();
            _shell.IsCameraPickOpen = false;
        }

        private async Task<byte[]> CaptureAsync()
        {
            if (_pending != null)
            {
                return null;
            }

            var tcs = new TaskCompletionSource<byte[]>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = tcs;

            await RunOnUiAsync(() => _shell.IsCameraPickOpen = true).ConfigureAwait(true);

            // Let x:Bind / x:Load materialize CameraPickPresenter before we touch it.
            await YieldUiAsync().ConfigureAwait(true);

            ContentPresenter presenter = _getPresenter();
            if (presenter == null)
            {
                Complete(null);
                await RunOnUiAsync(() => _shell.IsCameraPickOpen = false).ConfigureAwait(true);
                return await tcs.Task.ConfigureAwait(true);
            }

            await RunOnUiAsync(() =>
            {
                CameraPickView overlay = EnsureOverlay(presenter);
                var pickVm = new CameraPickViewModel();
                overlay.CloseRequested -= OnCloseRequested;
                overlay.PhotoCaptured -= OnPhotoCaptured;
                overlay.ViewModel = pickVm;
                overlay.CloseRequested += OnCloseRequested;
                overlay.PhotoCaptured += OnPhotoCaptured;
                presenter.Visibility = Visibility.Visible;

                _localBack?.Dispose();
                if (_navigator != null)
                {
                    _localBack = _navigator.PushLocalBack(() =>
                    {
                        Complete(null);
                        return true;
                    });
                }
            }).ConfigureAwait(true);

            try
            {
                return await tcs.Task.ConfigureAwait(true);
            }
            finally
            {
                await RunOnUiAsync(() =>
                {
                    TeardownOverlay();
                    _shell.IsCameraPickOpen = false;
                }).ConfigureAwait(true);
            }
        }

        private CameraPickView EnsureOverlay(ContentPresenter presenter)
        {
            if (_overlay != null)
            {
                return _overlay;
            }

            _overlay = new CameraPickView();
            presenter.Content = _overlay;
            return _overlay;
        }

        private void OnCloseRequested(object sender, EventArgs e) => Complete(null);

        private void OnPhotoCaptured(object sender, CameraPhotoCapturedEventArgs e)
        {
            Complete(e?.Bytes);
        }

        private void Complete(byte[] bytes)
        {
            TaskCompletionSource<byte[]> pending = _pending;
            _pending = null;
            pending?.TrySetResult(bytes);
        }

        private void TeardownOverlay()
        {
            _localBack?.Dispose();
            _localBack = null;

            if (_overlay != null)
            {
                _overlay.CloseRequested -= OnCloseRequested;
                _overlay.PhotoCaptured -= OnPhotoCaptured;
                _overlay.ViewModel = null;
            }

            ContentPresenter presenter = _getPresenter();
            if (presenter != null)
            {
                presenter.Content = null;
                presenter.Visibility = Visibility.Collapsed;
            }

            _overlay = null;
        }

        private static async Task RunOnUiAsync(Action action)
        {
            CoreDispatcher dispatcher = CoreApplication.MainView.CoreWindow.Dispatcher;
            if (dispatcher.HasThreadAccess)
            {
                action();
                return;
            }

            var tcs = new TaskCompletionSource<object>();
            await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                try
                {
                    action();
                    tcs.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
            await tcs.Task.ConfigureAwait(true);
        }

        private static async Task YieldUiAsync()
        {
            var tcs = new TaskCompletionSource<object>();
            await CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(
                CoreDispatcherPriority.Low,
                () => tcs.TrySetResult(null));
            await tcs.Task.ConfigureAwait(true);
        }
    }
}
