using Unison.Core.ViewModels;
using Xunit;

namespace Unison.Core.Tests.ViewModels
{
    public sealed class CameraPickViewModelTests
    {
        [Fact]
        public void ReportDevices_sets_IsMultipleCameras_when_more_than_one()
        {
            var vm = new CameraPickViewModel();

            vm.ReportDevices(cameraCount: 2, flashSupported: true);

            Assert.True(vm.IsMultipleCameras);
            Assert.True(vm.IsFlashAvailable);
            Assert.True(vm.SwitchCameraCommand.CanExecute(null));
        }

        [Fact]
        public void ReportDevices_hides_switch_for_a_single_camera()
        {
            var vm = new CameraPickViewModel();

            vm.ReportDevices(cameraCount: 1, flashSupported: false);

            Assert.False(vm.IsMultipleCameras);
            Assert.False(vm.IsFlashAvailable);
            Assert.False(vm.SwitchCameraCommand.CanExecute(null));
        }

        [Fact]
        public void ReportPhoto_raises_PhotoCaptured_with_the_bytes()
        {
            var vm = new CameraPickViewModel();
            byte[] seen = null;
            vm.PhotoCaptured += (_, e) => seen = e.Bytes;
            byte[] jpeg = { 1, 2, 3 };

            vm.ReportPhoto(jpeg);

            Assert.Same(jpeg, seen);
        }

        [Fact]
        public void Cancel_closes_and_drops_a_late_photo()
        {
            var vm = new CameraPickViewModel();
            vm.IsPreviewReady = true;
            bool closed = false;
            byte[] seen = null;
            vm.CloseRequested += (_, __) => closed = true;
            vm.PhotoCaptured += (_, e) => seen = e.Bytes;

            vm.Cancel();
            vm.ReportPhoto(new byte[] { 9, 9 });

            Assert.True(closed);
            Assert.True(vm.IsCancelled);
            Assert.False(vm.IsPreviewReady);
            Assert.Null(seen);
            Assert.False(vm.CaptureCommand.CanExecute(null));
        }

        [Fact]
        public void Cancel_is_idempotent()
        {
            var vm = new CameraPickViewModel();
            int closes = 0;
            vm.CloseRequested += (_, __) => closes++;

            vm.Cancel();
            vm.Cancel();

            Assert.Equal(1, closes);
        }
    }
}
