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
    }
}
