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

        [Fact]
        public void RefineForCaller_incoming_completed_becomes_answered_elsewhere()
        {
            Assert.Equal(
                CallEventChromeKind.AnsweredElsewhere,
                CallEventChromeMap.RefineForCaller(CallEventChromeKind.Completed, callerIsSelf: false));
        }

        [Fact]
        public void RefineForCaller_outgoing_completed_stays_completed()
        {
            Assert.Equal(
                CallEventChromeKind.Completed,
                CallEventChromeMap.RefineForCaller(CallEventChromeKind.Completed, callerIsSelf: true));
        }

        [Theory]
        [InlineData(CallEventChromeKind.Missed, false, "Missed voice call")]
        [InlineData(CallEventChromeKind.Missed, true, "Missed video call")]
        [InlineData(CallEventChromeKind.Completed, false, "Voice call")]
        [InlineData(CallEventChromeKind.Completed, true, "Video call")]
        [InlineData(CallEventChromeKind.AnsweredElsewhere, false, "Voice call")]
        public void ResolveContent_picks_voice_or_video_fallback(
            CallEventChromeKind kind,
            bool isVideo,
            string expected)
        {
            Assert.Equal(expected, CallEventChromeMap.ResolveContent(kind, isVideo, strings: null));
        }

        [Fact]
        public void ResolveSubtitle_missed_is_tap_to_return()
        {
            Assert.Equal(
                "Tap to call back",
                CallEventChromeMap.ResolveSubtitle(CallEventChromeKind.Missed, 0, strings: null));
        }

        [Fact]
        public void ResolveSubtitle_answered_elsewhere()
        {
            Assert.Equal(
                "Answered on another device",
                CallEventChromeMap.ResolveSubtitle(
                    CallEventChromeKind.AnsweredElsewhere,
                    0,
                    strings: null));
        }

        [Fact]
        public void ResolveSubtitle_completed_with_duration()
        {
            string subtitle = CallEventChromeMap.ResolveSubtitle(
                CallEventChromeKind.Completed,
                44,
                strings: null);
            Assert.Contains("44", subtitle);
        }
    }
}
