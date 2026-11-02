using Unison.Core.Helpers.Calls;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class CallEventChromeMapTests
    {
        [Theory]
        [InlineData("Timeout", CallEventChromeKind.Missed)]
        [InlineData("Reject", CallEventChromeKind.Missed)]
        [InlineData("Accept", CallEventChromeKind.Completed)]
        [InlineData("Terminate", CallEventChromeKind.Completed)]
        public void FromStatusName_maps_terminal_statuses(string status, CallEventChromeKind expected)
        {
            Assert.Equal(expected, CallEventChromeMap.FromStatusName(status));
        }

        [Fact]
        public void FromStatusName_maps_offer_to_incoming()
        {
            Assert.Equal(CallEventChromeKind.Incoming, CallEventChromeMap.FromStatusName("Offer"));
        }

        [Theory]
        [InlineData("Ringing")]
        [InlineData("")]
        [InlineData(null)]
        public void FromStatusName_ignores_ringing_and_empty(string status)
        {
            Assert.Null(CallEventChromeMap.FromStatusName(status));
        }

        [Theory]
        [InlineData(CallEventChromeKind.Missed, false, "Missed voice call")]
        [InlineData(CallEventChromeKind.Missed, true, "Missed video call")]
        [InlineData(CallEventChromeKind.Completed, false, "Voice call")]
        [InlineData(CallEventChromeKind.Completed, true, "Video call")]
        public void ResolveContent_picks_voice_or_video_fallback(
            CallEventChromeKind kind,
            bool isVideo,
            string expected)
        {
            Assert.Equal(expected, CallEventChromeMap.ResolveContent(kind, isVideo, strings: null));
        }
    }
}
