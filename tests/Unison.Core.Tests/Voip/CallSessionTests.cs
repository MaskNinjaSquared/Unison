using System;
using System.Threading.Tasks;
using Unison.Voip.Null;
using Unison.Voip.Session;
using Xunit;

namespace Unison.Core.Tests.Voip
{
    public sealed class CallSessionTests
    {
        [Fact]
        public async Task Reject_sends_signaling_and_ends_an_inbound_ring()
        {
            var channel = new NullCallSignalingChannel();
            var session = new CallSession(
                "call-1",
                "15551234567@s.whatsapp.net",
                "15551234567@s.whatsapp.net",
                isVideo: false,
                inbound: true,
                channel);

            Assert.Equal(CallSessionState.Ringing, session.State);

            await session.RejectAsync();

            Assert.Equal(1, channel.RejectCount);
            Assert.Equal("call-1", channel.LastCallId);
            Assert.Equal(CallSessionState.Ended, session.State);
        }

        [Fact]
        public async Task Hangup_terminates_and_ends_the_session()
        {
            var channel = new NullCallSignalingChannel();
            var session = new CallSession(
                "call-2",
                "15557654321@s.whatsapp.net",
                "15550001111@s.whatsapp.net",
                isVideo: false,
                inbound: false,
                channel);

            await session.HangupAsync("timeout");

            Assert.Equal(1, channel.TerminateCount);
            Assert.Equal("timeout", channel.LastReason);
            Assert.Equal(CallSessionState.Ended, session.State);
        }

        [Fact]
        public async Task Answer_sends_accept_and_moves_inbound_ring_to_connecting()
        {
            var channel = new NullCallSignalingChannel();
            var session = new CallSession(
                "call-3",
                "1@s.whatsapp.net",
                "1@s.whatsapp.net",
                isVideo: true,
                inbound: true,
                channel);

            await session.AnswerAsync();

            Assert.Equal(1, channel.AcceptCount);
            Assert.True(channel.LastAcceptWasVideo);
            Assert.Equal(CallSessionState.Connecting, session.State);
            Assert.Equal(Unison.Voip.Engine.CallEnginePhase.Connecting, session.Engine.Phase);
            Assert.False(session.Engine.IsMediaReady);
        }

        [Fact]
        public async Task Reject_on_outbound_throws()
        {
            var session = new CallSession(
                "call-4",
                "1@s.whatsapp.net",
                "1@s.whatsapp.net",
                isVideo: false,
                inbound: false,
                NullCallSignalingChannel.Instance);

            await Assert.ThrowsAsync<InvalidOperationException>(() => session.RejectAsync());
        }
    }
}
