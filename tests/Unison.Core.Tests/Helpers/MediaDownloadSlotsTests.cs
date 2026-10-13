using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class MediaDownloadSlotsTests
    {
        [Fact]
        public void A_phone_downloads_one_file_at_a_time_and_a_desktop_three()
        {
            Assert.Equal(1, MediaDownloadSlots.ForDevice(isMobile: true));
            Assert.Equal(3, MediaDownloadSlots.ForDevice(isMobile: false));
        }
    }
}
