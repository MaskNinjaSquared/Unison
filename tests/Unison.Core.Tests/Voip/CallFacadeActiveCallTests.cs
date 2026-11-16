using System;
using System.Threading.Tasks;
using Unison.Core.Models;
using Unison.Voip.Null;
using Unison.WhatsAppClient.Calls;
using Xunit;

namespace Unison.Core.Tests.Voip
{
    public sealed class CallFacadeActiveCallTests
    {
        private const string Peer = "5511988887777@s.whatsapp.net";
        private const string Self = "5511900000000@s.whatsapp.net";
        private static readonly DateTime Now = new DateTime(2026, 11, 16, 20, 0, 0, DateTimeKind.Utc);

        private static CallFacade Create(NullCallSignalingChannel channel) =>
            new CallFacade(channel, jid => string.Equals(jid, Self, StringComparison.Ordinal), () => Now);

        private static CallEventNotifyRequest Event(CallEventChromeKind kind, string callId = "c1", string caller = Peer) =>
            new CallEventNotifyRequest
            {
                CallId = callId,
                ChatJid = Peer,
                CallerJid = caller,
                Kind = kind,
                TimestampUtc = Now.AddSeconds(-1)
            };

        [Fact]
        public void Fresh_inbound_offer_becomes_a_ringing_active_call()
        {
            var facade = Create(new NullCallSignalingChannel());
            int changes = 0;
            facade.ActiveCallChanged += (_, __) => changes++;

            facade.NotifyCallEvents(new[] { Event(CallEventChromeKind.Incoming) });

            Assert.NotNull(facade.ActiveCall);
            Assert.Equal("c1", facade.ActiveCall.CallId);
            Assert.Equal(Peer, facade.ActiveCall.PeerJid);
            Assert.Equal(ActiveCallPhase.Ringing, facade.ActiveCall.Phase);
            Assert.True(facade.ActiveCall.IsInbound);
            Assert.Equal(1, changes);
        }

        [Fact]
        public void Own_offer_from_the_phone_does_not_ring()
        {
            var facade = Create(new NullCallSignalingChannel());

            facade.NotifyCallEvents(new[] { Event(CallEventChromeKind.Incoming, caller: Self) });

            Assert.Null(facade.ActiveCall);
        }

        [Fact]
        public void Answered_elsewhere_clears_the_ring_without_signaling()
        {
            var channel = new NullCallSignalingChannel();
            var facade = Create(channel);
            facade.NotifyCallEvents(new[] { Event(CallEventChromeKind.Incoming) });

            facade.NotifyCallEvents(new[] { Event(CallEventChromeKind.AnsweredElsewhere) });

            Assert.Null(facade.ActiveCall);
            Assert.Equal(0, channel.RejectCount);
            Assert.Equal(0, channel.TerminateCount);
        }

        [Fact]
        public async Task End_while_ringing_rejects_the_offer()
        {
            var channel = new NullCallSignalingChannel();
            var facade = Create(channel);
            facade.NotifyCallEvents(new[] { Event(CallEventChromeKind.Incoming) });

            await facade.EndActiveCallAsync();

            Assert.Equal(1, channel.RejectCount);
            Assert.Equal("c1", channel.LastCallId);
            Assert.Null(facade.ActiveCall);
        }

        [Fact]
        public async Task End_with_nothing_up_sends_nothing()
        {
            var channel = new NullCallSignalingChannel();
            var facade = Create(channel);

            await facade.EndActiveCallAsync();

            Assert.Equal(0, channel.RejectCount);
            Assert.Equal(0, channel.TerminateCount);
        }

        [Fact]
        public void Audio_toggles_are_ignored_while_only_ringing()
        {
            var facade = Create(new NullCallSignalingChannel());
            facade.NotifyCallEvents(new[] { Event(CallEventChromeKind.Incoming) });

            facade.SetMuted(true);
            facade.SetSpeakerOn(true);
            facade.SetBluetoothOn(true);

            Assert.False(facade.ActiveCall.IsMuted);
            Assert.False(facade.ActiveCall.IsSpeakerOn);
            Assert.False(facade.ActiveCall.IsBluetoothOn);
        }

        [Fact]
        public void Media_is_still_unavailable()
        {
            Assert.False(Create(new NullCallSignalingChannel()).IsMediaAvailable);
        }
    }
}
