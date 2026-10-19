using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class MediaCacheCleanPlanTests
    {
        [Fact]
        public void Blank_and_repeated_paths_are_dropped()
        {
            var entries = new[]
            {
                new CachedMediaEntry { LocalUri = "ms-appdata:///local/MediaCache/Images/a.jpg", PosterUri = "  " },
                new CachedMediaEntry { LocalUri = "ms-appdata:///local/MediaCache/Images/a.jpg", PosterUri = "ms-appdata:///local/MediaCache/VideoPosters/a.jpg" },
                new CachedMediaEntry { LocalUri = null }
            };

            Assert.Equal(
                new[]
                {
                    "ms-appdata:///local/MediaCache/Images/a.jpg",
                    "ms-appdata:///local/MediaCache/VideoPosters/a.jpg"
                },
                MediaCacheCleanPlan.DistinctUris(entries));
        }

        [Fact]
        public void Formats_bytes()
        {
            Assert.Equal("0 B", ByteSizeFormat.Format(0));
            Assert.Equal("1.0 KB", ByteSizeFormat.Format(1024));
            Assert.Equal("1.5 MB", ByteSizeFormat.Format(1024 * 1024 + 1024 * 512));
        }
    }
}
