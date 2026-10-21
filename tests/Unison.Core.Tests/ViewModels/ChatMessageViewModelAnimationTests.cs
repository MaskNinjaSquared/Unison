using Unison.Core.Models;
using Unison.Core.ViewModels;
using Xunit;

namespace Unison.Core.Tests.ViewModels
{
    public class ChatMessageViewModelAnimationTests
    {
        [Fact]
        public void Gif_local_shows_static_button_not_video_play()
        {
            var model = new ChatMessage
            {
                Kind = ChatMessageKind.Gif,
                VideoUri = "ms-appdata:///local/MediaCache/Videos/gif.mp4"
            };
            var vm = new ChatMessageViewModel(model, deviceCategory: DeviceCategory.Low);

            Assert.True(vm.IsGif);
            Assert.True(vm.ShowGifSurface);
            Assert.True(vm.ShowGifPlayButton);
            Assert.False(vm.IsGifPlaying);
            Assert.False(vm.ShowVideoPlayOverlay);
            Assert.False(vm.ResumeGifWhenVisible);
        }

        [Fact]
        public void Play_sets_started_and_low_resumes_when_visible()
        {
            var model = new ChatMessage
            {
                Kind = ChatMessageKind.Gif,
                VideoUri = "ms-appdata:///local/MediaCache/Videos/gif.mp4"
            };
            var vm = new ChatMessageViewModel(model, deviceCategory: DeviceCategory.Low);

            vm.StartGifPlayback();

            Assert.True(vm.HasStartedGifPlayback);
            Assert.True(vm.IsGifPlaying);
            Assert.False(vm.ShowGifPlayButton);
            Assert.True(vm.ResumeGifWhenVisible);
        }

        [Fact]
        public void Unsupported_never_resumes_after_play()
        {
            var model = new ChatMessage
            {
                Kind = ChatMessageKind.Gif,
                VideoUri = "ms-appdata:///local/MediaCache/Videos/gif.mp4"
            };
            var vm = new ChatMessageViewModel(model, deviceCategory: DeviceCategory.Unsupported);

            vm.StartGifPlayback();
            vm.PauseGifPlayback();

            Assert.True(vm.HasStartedGifPlayback);
            Assert.False(vm.IsGifPlaying);
            Assert.False(vm.ResumeGifWhenVisible);
            Assert.True(vm.ShowGifPlayButton);
        }

        [Fact]
        public void Normal_video_keeps_play_overlay()
        {
            var model = new ChatMessage
            {
                Kind = ChatMessageKind.Video,
                VideoUri = "ms-appdata:///local/MediaCache/Videos/clip.mp4",
                VideoDurationSeconds = 12
            };
            var vm = new ChatMessageViewModel(model);

            Assert.False(vm.IsGif);
            Assert.False(vm.ShowGifSurface);
            Assert.True(vm.ShowVideoPlayOverlay);
            Assert.True(vm.ShowVideoFooterDuration);
        }
    }
}
