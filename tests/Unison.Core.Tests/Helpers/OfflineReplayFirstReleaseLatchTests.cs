using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class OfflineReplayFirstReleaseLatchTests
    {
        [Fact]
        public void First_mark_succeeds_once_until_reset()
        {
            var latch = new OfflineReplayFirstReleaseLatch();

            Assert.True(latch.TryMarkFirstRelease());
            Assert.False(latch.TryMarkFirstRelease());
        }

        [Fact]
        public void Reset_on_open_allows_another_first_release()
        {
            var latch = new OfflineReplayFirstReleaseLatch();
            Assert.True(latch.TryMarkFirstRelease());

            latch.ResetForNewOpen();

            Assert.True(latch.TryMarkFirstRelease());
        }
    }
}
