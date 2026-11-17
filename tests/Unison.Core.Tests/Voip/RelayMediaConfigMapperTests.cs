using Unison.Core.Models;
using Unison.Voip.Engine.Relay;
using Unison.WhatsAppClient.Calls;
using Xunit;

namespace Unison.Core.Tests.Voip
{
    public sealed class RelayMediaConfigMapperTests
    {
        [Fact]
        public void Copies_call_key_and_prefers_relay_peer_over_signaling_peer()
        {
            var hints = new CallMediaHints
            {
                CallKey = new byte[] { 7 },
                RelayKey = new byte[] { 9 },
                RelayPeerJid = "222:7@lid"
            };

            RelayMediaConfig config = RelayMediaConfigMapper.From(hints, "cid", "111:0@lid", "222:0@lid", inbound: true);

            Assert.Equal(new byte[] { 7 }, config.CallKey);
            Assert.Equal("222:7@lid", config.PeerLid);
            Assert.Equal("111:0@lid", config.SelfLid);
        }

        [Fact]
        public void Falls_back_to_signaling_peer_without_relay_peer()
        {
            var hints = new CallMediaHints { RelayKey = new byte[] { 9 } };

            RelayMediaConfig config = RelayMediaConfigMapper.From(hints, "cid", "111:0@lid", "222:0@lid", inbound: false);

            Assert.Equal("222:0@lid", config.PeerLid);
        }
    }
}
