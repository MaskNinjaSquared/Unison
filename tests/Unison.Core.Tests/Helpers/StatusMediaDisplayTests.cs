using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class StatusMediaDisplayTests
    {
        [Fact]
        public void ResolveUri_prefers_full_download_over_thumb()
        {
            var item = new HistoryStatus
            {
                MediaLocalUri = "ms-appdata:///local/MediaCache/Images/x_thumb.jpg",
                MediaPosterUri = "ms-appdata:///local/MediaCache/Images/y_thumb.jpg"
            };

            string uri = StatusMediaDisplay.ResolveUri(
                "ms-appdata:///local/MediaCache/Videos/full.mp4",
                item);

            Assert.Equal("ms-appdata:///local/MediaCache/Videos/full.mp4", uri);
        }

        [Fact]
        public void ResolveUri_falls_back_to_poster_then_local_when_download_missing()
        {
            var item = new HistoryStatus
            {
                MediaPosterUri = "ms-appdata:///local/MediaCache/Images/p_thumb.jpg",
                MediaLocalUri = "ms-appdata:///local/MediaCache/Images/l_thumb.jpg"
            };

            Assert.Equal(item.MediaPosterUri, StatusMediaDisplay.ResolveUri(null, item));
            item.MediaPosterUri = null;
            Assert.Equal(item.MediaLocalUri, StatusMediaDisplay.ResolveUri(string.Empty, item));
        }

        [Fact]
        public void ShowAsVideo_requires_non_thumb_uri()
        {
            Assert.True(StatusMediaDisplay.ShowAsVideo(
                ChatPreviewKind.Video,
                "ms-appdata:///local/MediaCache/Videos/a.mp4"));
            Assert.False(StatusMediaDisplay.ShowAsVideo(
                ChatPreviewKind.Video,
                "ms-appdata:///local/MediaCache/Images/a_thumb.jpg"));
            Assert.False(StatusMediaDisplay.ShowAsVideo(ChatPreviewKind.Image, "x.jpg"));
        }

        [Fact]
        public void ShowAsImage_includes_video_thumb_fallback()
        {
            Assert.True(StatusMediaDisplay.ShowAsImage(
                ChatPreviewKind.Image,
                "ms-appdata:///local/MediaCache/Images/a_thumb.jpg"));
            Assert.True(StatusMediaDisplay.ShowAsImage(
                ChatPreviewKind.Video,
                "ms-appdata:///local/MediaCache/Images/a_thumb.jpg"));
            Assert.False(StatusMediaDisplay.ShowAsImage(
                ChatPreviewKind.Video,
                "ms-appdata:///local/MediaCache/Videos/a.mp4"));
        }
    }
}
