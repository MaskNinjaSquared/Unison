using System.Collections.Generic;
using Unison.Socket.Models;
using Unison.WhatsAppClient.Calls;
using Xunit;

namespace Unison.Core.Tests.Voip
{
    public sealed class CallMediaHintsMapperTests
    {
        [Fact]
        public void Offer_without_key_relay_or_probes_maps_to_null()
        {
            var call = new CallOffer { ActionTag = "terminate" };

            Assert.Null(CallMediaHintsMapper.From(call));
        }

        [Fact]
        public void Copies_call_key_relay_and_probes()
        {
            var relay = new CallRelayInfo { Key = new byte[] { 9 }, PeerJid = "peer@lid" };
            relay.Tokens.Add(new byte[] { 1 });
            relay.Tokens.Add(null);
            relay.Endpoints.Add(new CallRelayEndpoint
            {
                RelayId = 3,
                RelayName = "gru1",
                TokenId = 0,
                AuthTokenId = 1,
                IsFna = true,
                Ipv4 = "1.2.3.4",
                Port = 3478
            });
            var call = new CallOffer
            {
                CallKey = new byte[] { 7, 7 },
                Relay = relay,
                RelayLatencyProbes = new List<CallRelayLatencyProbe>
                {
                    new CallRelayLatencyProbe { LatencyMs = 25, RelayName = "gru1", AddressBytes = new byte[] { 1, 2, 3, 4, 0x0d, 0x96 } }
                }
            };

            var hints = CallMediaHintsMapper.From(call);

            Assert.Equal(new byte[] { 7, 7 }, hints.CallKey);
            Assert.Equal(new byte[] { 9 }, hints.RelayKey);
            Assert.Equal("peer@lid", hints.RelayPeerJid);
            Assert.Equal(2, hints.RelayTokens.Count);
            Assert.Null(hints.RelayTokens[1]);
            var e = Assert.Single(hints.RelayEndpoints);
            Assert.True(e.IsFna);
            Assert.Equal("1.2.3.4", e.Ipv4);
            Assert.Equal(3478, e.Port);
            Assert.Equal(1u, e.AuthTokenId);
            var probe = Assert.Single(hints.LatencyProbes);
            Assert.Equal(25u, probe.LatencyMs);
            Assert.Equal("gru1", probe.RelayName);
        }
    }
}
