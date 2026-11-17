using Unison.Core.Helpers.Calls;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class VoipSystemCallWakePolicyTests
    {
        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void System_ui_only_when_window_is_hidden(bool windowVisible, bool expectRaise)
        {
            Assert.Equal(expectRaise, VoipSystemCallWakePolicy.ShouldRaiseSystemUi(windowVisible));
        }

        [Fact]
        public void Hidden_inbound_ring_raises_system_ui()
        {
            Assert.Equal(
                VoipSystemCallSyncAction.Ring,
                VoipSystemCallWakePolicy.Decide(
                    Ringing("c1"),
                    currentSystemCallId: null,
                    windowVisible: false));
        }

        [Fact]
        public void Visible_inbound_ring_stays_in_app_only()
        {
            Assert.Equal(
                VoipSystemCallSyncAction.None,
                VoipSystemCallWakePolicy.Decide(
                    Ringing("c1"),
                    currentSystemCallId: null,
                    windowVisible: true));
        }

        [Fact]
        public void Same_system_call_is_not_raised_twice()
        {
            Assert.Equal(
                VoipSystemCallSyncAction.None,
                VoipSystemCallWakePolicy.Decide(
                    Ringing("c1"),
                    currentSystemCallId: "c1",
                    windowVisible: false));
        }

        [Fact]
        public void Becoming_visible_while_system_rings_ends_system_ui()
        {
            Assert.Equal(
                VoipSystemCallSyncAction.End,
                VoipSystemCallWakePolicy.Decide(
                    Ringing("c1"),
                    currentSystemCallId: "c1",
                    windowVisible: true));
        }

        [Fact]
        public void Cleared_active_call_ends_system_ui()
        {
            Assert.Equal(
                VoipSystemCallSyncAction.End,
                VoipSystemCallWakePolicy.Decide(
                    call: null,
                    currentSystemCallId: "c1",
                    windowVisible: false));
        }

        [Fact]
        public void Outbound_or_active_phase_does_not_ring_system_ui()
        {
            var active = new ActiveCallSnapshot(
                "c1",
                "5511@s.whatsapp.net",
                "5511@s.whatsapp.net",
                isInbound: false,
                isVideo: false,
                ActiveCallPhase.Dialing);

            Assert.Equal(
                VoipSystemCallSyncAction.None,
                VoipSystemCallWakePolicy.Decide(active, null, windowVisible: false));
        }

        private static ActiveCallSnapshot Ringing(string callId) =>
            new ActiveCallSnapshot(
                callId,
                "5511999999999@s.whatsapp.net",
                "5511999999999@s.whatsapp.net",
                isInbound: true,
                isVideo: false,
                ActiveCallPhase.Ringing);
    }
}
