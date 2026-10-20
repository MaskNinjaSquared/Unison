using Unison.Core.Models;
using Unison.Core.ViewModels;
using Xunit;

namespace Unison.Core.Tests.ViewModels
{
    public class ChatMessageViewModelStickerViewerTests
    {
        [Fact]
        public void Sticker_with_local_file_does_not_open_image_viewer()
        {
            var vm = new ChatMessageViewModel(new ChatMessage
            {
                Kind = ChatMessageKind.Sticker,
                ImageUri = "ms-appdata:///local/stickers/a.webp"
            });

            Assert.True(vm.HasImage);
            Assert.True(vm.ShowStickerImage);
            Assert.False(vm.CanOpenImageViewer);
        }

        [Fact]
        public void Photo_with_local_file_opens_image_viewer()
        {
            var vm = new ChatMessageViewModel(new ChatMessage
            {
                Kind = ChatMessageKind.Image,
                ImageUri = "ms-appdata:///local/photos/a.jpg"
            });

            Assert.True(vm.CanOpenImageViewer);
            Assert.False(vm.ShowStickerImage);
        }
    }
}
