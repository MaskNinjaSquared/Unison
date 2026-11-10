using System.Threading.Tasks;
using Unison.Core.Services;
using Xunit;

namespace Unison.Core.Tests.Services
{
    public class CameraPickServiceTests
    {
        [Fact]
        public async Task Pick_without_ui_returns_null()
        {
            var sut = new CameraPickService();
            Assert.Null(await sut.PickJpegAsync());
        }

        [Fact]
        public async Task Pick_returns_bytes_from_ui_delegate()
        {
            var sut = new CameraPickService();
            byte[] expected = { 1, 2, 3 };
            sut.AttachUiCapture(() => Task.FromResult(expected));

            byte[] actual = await sut.PickJpegAsync();

            Assert.Same(expected, actual);
        }

        [Fact]
        public async Task Pick_while_busy_returns_null()
        {
            var sut = new CameraPickService();
            var hold = new TaskCompletionSource<byte[]>();
            sut.AttachUiCapture(() => hold.Task);

            Task<byte[]> first = sut.PickJpegAsync();
            byte[] second = await sut.PickJpegAsync();

            Assert.Null(second);
            hold.TrySetResult(new byte[] { 9 });
            Assert.Equal(new byte[] { 9 }, await first);
        }

        [Fact]
        public async Task Detach_while_open_completes_pick_with_null()
        {
            var sut = new CameraPickService();
            var hold = new TaskCompletionSource<byte[]>();
            sut.AttachUiCapture(() => hold.Task);

            Task<byte[]> pick = sut.PickJpegAsync();
            sut.DetachUiCapture();

            Assert.Null(await pick);
        }

        [Fact]
        public async Task Cancelled_ui_result_returns_null()
        {
            var sut = new CameraPickService();
            sut.AttachUiCapture(() => Task.FromResult<byte[]>(null));

            Assert.Null(await sut.PickJpegAsync());
        }
    }
}
