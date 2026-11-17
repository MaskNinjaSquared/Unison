using System;
using Unison.Voip.Engine;
using Xunit;

namespace Unison.Core.Tests.Voip
{
    public sealed class CallEngineTests
    {
        [Fact]
        public void Inbound_starts_ringing_and_is_not_media_ready()
        {
            var engine = new CallEngine("c1", inbound: true);

            Assert.Equal(CallEnginePhase.Ringing, engine.Phase);
            Assert.False(engine.IsMediaReady);
            Assert.False(engine.AcceptPending);
        }

        [Fact]
        public void MarkAnswerPending_waits_for_mute_v2_then_consumes_once()
        {
            var engine = new CallEngine("c1", inbound: true);

            engine.MarkAnswerPending();

            Assert.Equal(CallEnginePhase.Connecting, engine.Phase);
            Assert.True(engine.AcceptPending);
            Assert.True(engine.TryConsumeMuteV2ForAccept());
            Assert.False(engine.AcceptPending);
            Assert.True(engine.MuteV2Seen);
            Assert.False(engine.TryConsumeMuteV2ForAccept());
        }

        [Fact]
        public void MarkConnecting_clears_accept_pending()
        {
            var engine = new CallEngine("c1", inbound: true);
            engine.MarkAnswerPending();

            engine.MarkConnecting();

            Assert.Equal(CallEnginePhase.Connecting, engine.Phase);
            Assert.False(engine.AcceptPending);
        }

        [Fact]
        public void OnRelayHint_stores_trimmed_value()
        {
            var engine = new CallEngine("c1", inbound: false);

            engine.OnRelayHint("  1.2.3.4:3478  ");

            Assert.Equal("1.2.3.4:3478", engine.RelayHint);
        }

        [Fact]
        public void Hangup_ends_and_blocks_further_work()
        {
            var engine = new CallEngine("c1", inbound: true);
            engine.Hangup();

            Assert.Equal(CallEnginePhase.Ended, engine.Phase);
            Assert.Throws<InvalidOperationException>(() => engine.MarkActive());
        }
    }
}
