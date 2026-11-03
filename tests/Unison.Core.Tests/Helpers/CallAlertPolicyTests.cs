using Unison.Core.Helpers.Calls;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class CallAlertPolicyTests
    {
        [Theory]
        [InlineData(CallEventChromeKind.Incoming, false, false, true)]
        [InlineData(CallEventChromeKind.Missed, false, false, true)]
        [InlineData(CallEventChromeKind.Completed, false, false, false)]
        [InlineData(CallEventChromeKind.Incoming, true, false, false)]
        [InlineData(CallEventChromeKind.Missed, true, false, false)]
        [InlineData(CallEventChromeKind.Incoming, false, true, false)]
        [InlineData(CallEventChromeKind.Missed, false, true, true)]
        public void ShouldShowToast_gates_self_kind_and_sync_quiet(
            CallEventChromeKind kind,
            bool callerIsSelf,
            bool historySyncQuiet,
            bool expected)
        {
            Assert.Equal(
                expected,
                CallAlertPolicy.ShouldShowToast(kind, callerIsSelf, historySyncQuiet));
        }

        [Theory]
        [InlineData(CallEventChromeKind.Missed, false, true)]
        [InlineData(CallEventChromeKind.Incoming, false, false)]
        [InlineData(CallEventChromeKind.Completed, false, false)]
        [InlineData(CallEventChromeKind.Missed, true, false)]
        public void ShouldUpdateLiveTile_only_missed_inbound(
            CallEventChromeKind kind,
            bool callerIsSelf,
            bool expected)
        {
            Assert.Equal(expected, CallAlertPolicy.ShouldUpdateLiveTile(kind, callerIsSelf));
        }

        [Theory]
        [InlineData(CallEventChromeKind.Missed, true)]
        [InlineData(CallEventChromeKind.Completed, true)]
        [InlineData(CallEventChromeKind.Incoming, true)]
        [InlineData(CallEventChromeKind.AnsweredElsewhere, true)]
        public void ShouldPersistCallLog_includes_incoming(CallEventChromeKind kind, bool expected)
        {
            Assert.Equal(expected, CallAlertPolicy.ShouldPersistCallLog(kind));
        }

        [Theory]
        [InlineData(CallEventChromeKind.Missed, true)]
        [InlineData(CallEventChromeKind.Completed, true)]
        [InlineData(CallEventChromeKind.AnsweredElsewhere, true)]
        [InlineData(CallEventChromeKind.Incoming, false)]
        public void ShouldWriteChatChrome_terminal_kinds(CallEventChromeKind kind, bool expected)
        {
            Assert.Equal(expected, CallAlertPolicy.ShouldWriteChatChrome(kind));
        }
    }
}
