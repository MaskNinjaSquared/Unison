using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Models;
using Unison.Voip.Abstractions;
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

        private static CallFacade Create(ICallSignalingChannel channel) =>
            new CallFacade(channel, jid => string.Equals(jid, Self, StringComparison.Ordinal), () => Now);

        private const string WireFrom = "call@call";

        private static CallEventNotifyRequest Event(
            CallEventChromeKind kind,
            string callId = "c1",
            string caller = Peer,
            string actionTag = null) =>
            new CallEventNotifyRequest
            {
                CallId = callId,
                ChatJid = Peer,
                CallerJid = caller,
                SignalingToJid = WireFrom,
                ActionTag = actionTag,
                Kind = kind,
                TimestampUtc = Now.AddSeconds(-1)
            };

        [Fact]
        public void Fresh_inbound_offer_becomes_a_ringing_active_call()
        {
            var channel = new NullCallSignalingChannel();
            var facade = Create(channel);
            int changes = 0;
            facade.ActiveCallChanged += (_, __) => changes++;

            facade.NotifyCallEvents(new[] { Event(CallEventChromeKind.Incoming) });

            Assert.NotNull(facade.ActiveCall);
            Assert.Equal("c1", facade.ActiveCall.CallId);
            Assert.Equal(Peer, facade.ActiveCall.PeerJid);
            Assert.Equal(ActiveCallPhase.Ringing, facade.ActiveCall.Phase);
            Assert.True(facade.ActiveCall.IsInbound);
            Assert.Equal(1, changes);
            Assert.Equal(1, channel.PreacceptCount);
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
        public void Mute_and_bluetooth_are_ignored_while_only_ringing()
        {
            var facade = Create(new NullCallSignalingChannel());
            facade.NotifyCallEvents(new[] { Event(CallEventChromeKind.Incoming) });

            facade.SetMuted(true);
            facade.SetBluetoothOn(true);

            Assert.False(facade.ActiveCall.IsMuted);
            Assert.False(facade.ActiveCall.IsBluetoothOn);
        }

        [Fact]
        public void Speaker_toggle_is_allowed_while_ringing()
        {
            var facade = Create(new NullCallSignalingChannel());
            facade.NotifyCallEvents(new[] { Event(CallEventChromeKind.Incoming) });

            facade.SetSpeakerOn(true);

            Assert.True(facade.ActiveCall.IsSpeakerOn);
        }

        [Fact]
        public async Task Answer_while_ringing_defers_accept_until_mute_v2()
        {
            var channel = new NullCallSignalingChannel();
            var facade = Create(channel);
            facade.NotifyCallEvents(new[] { Event(CallEventChromeKind.Incoming) });

            await facade.AnswerActiveCallAsync();

            Assert.NotNull(facade.ActiveCall);
            Assert.Equal(ActiveCallPhase.Connecting, facade.ActiveCall.Phase);
            Assert.Equal(0, channel.AcceptCount);
            Assert.Equal(WireFrom, channel.LastToJid);

            facade.NotifyCallEvents(new[]
            {
                Event(CallEventChromeKind.Incoming, actionTag: "mute_v2")
            });

            Assert.Equal(1, channel.AcceptCount);
            Assert.Equal(WireFrom, channel.LastToJid);
        }

        [Fact]
        public async Task End_after_answer_terminates_without_waiting_for_accept()
        {
            var channel = new NullCallSignalingChannel();
            var facade = Create(channel);
            facade.NotifyCallEvents(new[] { Event(CallEventChromeKind.Incoming) });
            await facade.AnswerActiveCallAsync();

            await facade.EndActiveCallAsync();

            Assert.Equal(0, channel.AcceptCount);
            Assert.Equal(0, channel.RejectCount);
            Assert.Equal(1, channel.TerminateCount);
            Assert.Equal(WireFrom, channel.LastToJid);
            Assert.Null(facade.ActiveCall);
        }

        [Fact]
        public async Task End_after_answer_still_clears_when_terminate_throws()
        {
            var channel = new ThrowingTerminateChannel();
            var facade = Create(channel);
            facade.NotifyCallEvents(new[] { Event(CallEventChromeKind.Incoming) });
            await facade.AnswerActiveCallAsync();

            await facade.EndActiveCallAsync();

            Assert.Equal(1, channel.TerminateAttempts);
            Assert.Null(facade.ActiveCall);
        }

        [Fact]
        public async Task Answer_with_nothing_up_is_a_no_op()
        {
            var facade = Create(new NullCallSignalingChannel());

            await facade.AnswerActiveCallAsync();

            Assert.Null(facade.ActiveCall);
        }

        private static CallEventNotifyRequest RelayLatency(string callId, params string[] relayNames)
        {
            var hints = new CallMediaHints();
            uint ms = 10;
            foreach (string name in relayNames)
            {
                hints.LatencyProbes.Add(new CallRelayLatencyCandidate
                {
                    LatencyMs = ms++,
                    RelayName = name,
                    AddressBytes = new byte[] { 1, 2, 3, 4, 0x0d, 0x96 }
                });
            }

            CallEventNotifyRequest e = Event(CallEventChromeKind.Incoming, callId, actionTag: "relaylatency");
            e.IsSignalingControl = true;
            e.MediaHints = hints;
            return e;
        }

        [Fact]
        public void Relaylatency_probes_for_the_ringing_call_are_echoed()
        {
            var channel = new NullCallSignalingChannel();
            var facade = Create(channel);
            facade.NotifyCallEvents(new[] { Event(CallEventChromeKind.Incoming) });

            facade.NotifyCallEvents(new[] { RelayLatency("c1", "gru1", "gru2") });

            Assert.Equal(2, channel.RelayLatencyCount);
            Assert.Equal(WireFrom, channel.LastToJid);
            Assert.Equal("gru2", channel.LastRelayName);
            Assert.Equal(11u, channel.LastLatencyMs);
            Assert.NotNull(facade.ActiveCall);
        }

        [Fact]
        public void Relaylatency_for_another_call_is_ignored()
        {
            var channel = new NullCallSignalingChannel();
            var facade = Create(channel);
            facade.NotifyCallEvents(new[] { Event(CallEventChromeKind.Incoming) });

            facade.NotifyCallEvents(new[] { RelayLatency("other", "gru1") });

            Assert.Equal(0, channel.RelayLatencyCount);
        }

        [Fact]
        public void Offer_media_hints_are_kept_and_later_relay_keeps_the_call_key()
        {
            var facade = Create(new NullCallSignalingChannel());
            CallEventNotifyRequest offer = Event(CallEventChromeKind.Incoming);
            offer.MediaHints = new CallMediaHints { CallKey = new byte[] { 7 } };
            facade.NotifyCallEvents(new[] { offer });

            CallEventNotifyRequest transport = Event(CallEventChromeKind.Incoming, actionTag: "transport");
            transport.IsSignalingControl = true;
            transport.MediaHints = new CallMediaHints { RelayKey = new byte[] { 9 } };
            transport.MediaHints.RelayEndpoints.Add(new CallMediaRelayEndpoint { RelayName = "gru1", IsFna = true });
            facade.NotifyCallEvents(new[] { transport });

            CallMediaHints hints = facade.GetMediaHints("c1");
            Assert.Equal(new byte[] { 7 }, hints.CallKey);
            Assert.Equal(new byte[] { 9 }, hints.RelayKey);
            Assert.Single(hints.RelayEndpoints);
        }

        [Fact]
        public void Media_hints_are_dropped_when_the_call_ends()
        {
            var facade = Create(new NullCallSignalingChannel());
            CallEventNotifyRequest offer = Event(CallEventChromeKind.Incoming);
            offer.MediaHints = new CallMediaHints { CallKey = new byte[] { 7 } };
            facade.NotifyCallEvents(new[] { offer });

            facade.NotifyCallEvents(new[] { Event(CallEventChromeKind.AnsweredElsewhere) });

            Assert.Null(facade.GetMediaHints("c1"));
        }

        [Fact]
        public void Media_is_still_unavailable()
        {
            Assert.False(Create(new NullCallSignalingChannel()).IsMediaAvailable);
        }

        [Fact]
        public async Task Start_outgoing_places_offer_and_dials()
        {
            var channel = new NullCallSignalingChannel();
            var facade = Create(channel);
            int changes = 0;
            facade.ActiveCallChanged += (_, __) => changes++;

            await facade.StartOutgoingAudioCallAsync(Peer);

            Assert.Equal(1, channel.OfferCount);
            Assert.Equal(Peer, channel.LastToJid);
            Assert.NotNull(facade.ActiveCall);
            Assert.Equal("test-call", facade.ActiveCall.CallId);
            Assert.Equal(ActiveCallPhase.Dialing, facade.ActiveCall.Phase);
            Assert.False(facade.ActiveCall.IsInbound);
            Assert.Equal(1, changes);
            Assert.Equal(new byte[32], facade.GetMediaHints("test-call").CallKey);
        }

        [Fact]
        public async Task Peer_accept_while_dialing_goes_active()
        {
            var channel = new NullCallSignalingChannel();
            var facade = Create(channel);
            await facade.StartOutgoingAudioCallAsync(Peer);

            CallEventNotifyRequest accept = Event(CallEventChromeKind.Completed, callId: "test-call");
            accept.ActionTag = "accept";
            facade.NotifyCallEvents(new[] { accept });

            Assert.NotNull(facade.ActiveCall);
            Assert.Equal(ActiveCallPhase.Active, facade.ActiveCall.Phase);
            Assert.True(facade.ActiveCall.ConnectedAtUtc.HasValue);
        }

        [Fact]
        public async Task End_while_dialing_terminates()
        {
            var channel = new NullCallSignalingChannel();
            var facade = Create(channel);
            await facade.StartOutgoingAudioCallAsync(Peer);

            await facade.EndActiveCallAsync();

            Assert.Equal(1, channel.TerminateCount);
            Assert.Equal("test-call", channel.LastCallId);
            Assert.Null(facade.ActiveCall);
        }

        private sealed class ThrowingTerminateChannel : ICallSignalingChannel
        {
            public int TerminateAttempts { get; private set; }

            public Task<CallOfferWireResult> OfferAudioAsync(
                string peerJid,
                CancellationToken cancellationToken) =>
                Task.FromResult(new CallOfferWireResult
                {
                    CallId = "throw-call",
                    PeerJid = peerJid,
                    CallCreatorJid = Self,
                    CallKey = new byte[32]
                });

            public Task PreacceptAsync(
                string callId,
                string toJid,
                string callCreatorJid,
                CancellationToken cancellationToken) =>
                Task.CompletedTask;

            public Task AcceptAsync(
                string callId,
                string toJid,
                string callCreatorJid,
                bool isVideo,
                CancellationToken cancellationToken) =>
                Task.CompletedTask;

            public Task RejectAsync(
                string callId,
                string toJid,
                string callCreatorJid,
                CancellationToken cancellationToken) =>
                Task.CompletedTask;

            public Task TerminateAsync(
                string callId,
                string toJid,
                string callCreatorJid,
                string reason,
                CancellationToken cancellationToken)
            {
                TerminateAttempts++;
                throw new InvalidOperationException("No live WhatsApp session for call signaling.");
            }

            public Task SendRelayLatencyAsync(
                string callId,
                string toJid,
                string callCreatorJid,
                uint latencyMs,
                string relayName,
                byte[] addressBytes,
                CancellationToken cancellationToken) =>
                Task.CompletedTask;
        }
    }
}
