using System;
using Unison.Core.Helpers.Calls;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ActiveCallEventPolicyTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 16, 20, 0, 0, DateTimeKind.Utc);

        private static CallEventNotifyRequest Offer(string callId = "c1", int ageSeconds = 2) =>
            new CallEventNotifyRequest
            {
                CallId = callId,
                ChatJid = "5511999990000@s.whatsapp.net",
                CallerJid = "5511999990000@s.whatsapp.net",
                Kind = CallEventChromeKind.Incoming,
                TimestampUtc = Now.AddSeconds(-ageSeconds)
            };

        private static ActiveCallSnapshot Ringing(string callId = "c1") =>
            new ActiveCallSnapshot(callId, "peer@s.whatsapp.net", "peer@s.whatsapp.net", isInbound: true, isVideo: false, ActiveCallPhase.Ringing);

        [Fact]
        public void Fresh_offer_from_someone_else_rings_when_idle()
        {
            Assert.Equal(
                ActiveCallEventAction.Ring,
                ActiveCallEventPolicy.Decide(Offer(), callerIsSelf: false, current: null, nowUtc: Now));
        }

        [Fact]
        public void Fresh_video_offer_does_not_ring_on_companion()
        {
            var video = Offer();
            video.IsVideo = true;
            Assert.Equal(
                ActiveCallEventAction.Ignore,
                ActiveCallEventPolicy.Decide(video, callerIsSelf: false, current: null, nowUtc: Now));
        }

        [Fact]
        public void Offer_from_self_never_rings()
        {
            Assert.Equal(
                ActiveCallEventAction.Ignore,
                ActiveCallEventPolicy.Decide(Offer(), callerIsSelf: true, current: null, nowUtc: Now));
        }

        [Fact]
        public void Stale_offer_from_offline_replay_does_not_ring()
        {
            var stale = Offer(ageSeconds: (int)ActiveCallEventPolicy.RingFreshWindow.TotalSeconds + 1);
            Assert.Equal(
                ActiveCallEventAction.Ignore,
                ActiveCallEventPolicy.Decide(stale, callerIsSelf: false, current: null, nowUtc: Now));
        }

        [Fact]
        public void Second_offer_while_a_call_is_up_does_not_replace_it()
        {
            Assert.Equal(
                ActiveCallEventAction.Ignore,
                ActiveCallEventPolicy.Decide(Offer("c2"), callerIsSelf: false, current: Ringing("c1"), nowUtc: Now));
        }

        [Theory]
        [InlineData(CallEventChromeKind.Missed)]
        [InlineData(CallEventChromeKind.Completed)]
        [InlineData(CallEventChromeKind.AnsweredElsewhere)]
        public void Terminal_status_for_the_active_call_ends_it(CallEventChromeKind kind)
        {
            var evt = Offer();
            evt.Kind = kind;
            Assert.Equal(
                ActiveCallEventAction.End,
                ActiveCallEventPolicy.Decide(evt, callerIsSelf: false, current: Ringing("c1"), nowUtc: Now));
        }

        [Fact]
        public void Terminal_status_for_another_call_is_ignored()
        {
            var evt = Offer("other");
            evt.Kind = CallEventChromeKind.Missed;
            Assert.Equal(
                ActiveCallEventAction.Ignore,
                ActiveCallEventPolicy.Decide(evt, callerIsSelf: false, current: Ringing("c1"), nowUtc: Now));
        }

        [Fact]
        public void Offer_without_call_id_is_ignored()
        {
            var evt = Offer();
            evt.CallId = " ";
            Assert.Equal(
                ActiveCallEventAction.Ignore,
                ActiveCallEventPolicy.Decide(evt, callerIsSelf: false, current: null, nowUtc: Now));
        }

        private static ActiveCallSnapshot Dialing(string callId = "c1") =>
            new ActiveCallSnapshot(
                callId,
                "peer@s.whatsapp.net",
                "self@lid",
                isInbound: false,
                isVideo: false,
                ActiveCallPhase.Dialing);

        [Fact]
        public void Outbound_accept_while_dialing_connects()
        {
            var evt = Offer();
            evt.Kind = CallEventChromeKind.Completed;
            evt.ActionTag = "accept";
            Assert.Equal(
                ActiveCallEventAction.Connect,
                ActiveCallEventPolicy.Decide(evt, callerIsSelf: false, current: Dialing(), nowUtc: Now));
        }

        [Theory]
        [InlineData(CallEventChromeKind.Missed)]
        [InlineData(CallEventChromeKind.AnsweredElsewhere)]
        public void Outbound_terminal_while_dialing_ends(CallEventChromeKind kind)
        {
            var evt = Offer();
            evt.Kind = kind;
            Assert.Equal(
                ActiveCallEventAction.End,
                ActiveCallEventPolicy.Decide(evt, callerIsSelf: false, current: Dialing(), nowUtc: Now));
        }

        [Fact]
        public void Outbound_terminate_while_dialing_ends()
        {
            var evt = Offer();
            evt.Kind = CallEventChromeKind.Completed;
            evt.ActionTag = "terminate";
            Assert.Equal(
                ActiveCallEventAction.End,
                ActiveCallEventPolicy.Decide(evt, callerIsSelf: false, current: Dialing(), nowUtc: Now));
        }
    }
}
