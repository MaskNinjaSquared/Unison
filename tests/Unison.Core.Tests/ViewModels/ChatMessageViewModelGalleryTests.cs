using Unison.Core.Models;
using Unison.Core.ViewModels;
using Xunit;

namespace Unison.Core.Tests.ViewModels
{
    public class ChatMessageViewModelGalleryTests
    {
        [Theory]
        [InlineData(0, "1/2")]
        [InlineData(1, "2/2")]
        public void Outgoing_batch_image_shows_its_position(int index, string expected)
        {
            var vm = new ChatMessageViewModel(new ChatMessage
            {
                IsFromMe = true,
                LocalMediaBatchId = "batch",
                LocalMediaBatchIndex = index,
                LocalMediaBatchCount = 2
            });

            Assert.True(vm.IsGalleryItem);
            Assert.Equal(expected, vm.GalleryPositionLabel);
        }

        [Fact]
        public void Single_image_has_no_gallery_badge()
        {
            var vm = new ChatMessageViewModel(new ChatMessage
            {
                IsFromMe = true,
                LocalMediaBatchId = "batch",
                LocalMediaBatchIndex = 0,
                LocalMediaBatchCount = 1
            });

            Assert.False(vm.IsGalleryItem);
            Assert.Equal(string.Empty, vm.GalleryPositionLabel);
        }
    }
}
