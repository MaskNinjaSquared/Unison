// =============================================================================
// CallMediaHintsMapper
//
// Socket CallOffer (callKey / relay / relaylatency probes) → Core CallMediaHints.
// =============================================================================
using System.Collections.Generic;
using Unison.Core.Models;
using Unison.Socket.Models;

namespace Unison.WhatsAppClient.Calls
{
    public static class CallMediaHintsMapper
    {
        /// <returns>Null when the stanza carried no media material.</returns>
        public static CallMediaHints From(CallOffer call)
        {
            bool hasProbes = call?.RelayLatencyProbes != null && call.RelayLatencyProbes.Count > 0;
            if (call == null || (call.CallKey == null && call.Relay == null && !hasProbes))
            {
                return null;
            }

            var hints = new CallMediaHints { CallKey = call.CallKey };
            if (call.Relay != null)
            {
                hints.RelayKey = call.Relay.Key;
                hints.RelayPeerJid = call.Relay.PeerJid;
                hints.RelayTokens = new List<byte[]>(call.Relay.Tokens);
                foreach (CallRelayEndpoint e in call.Relay.Endpoints)
                {
                    hints.RelayEndpoints.Add(new CallMediaRelayEndpoint
                    {
                        RelayId = e.RelayId,
                        RelayName = e.RelayName,
                        TokenId = e.TokenId,
                        AuthTokenId = e.AuthTokenId,
                        IsFna = e.IsFna,
                        Ipv4 = e.Ipv4,
                        Port = e.Port
                    });
                }
            }

            if (hasProbes)
            {
                foreach (CallRelayLatencyProbe p in call.RelayLatencyProbes)
                {
                    hints.LatencyProbes.Add(new CallRelayLatencyCandidate
                    {
                        LatencyMs = p.LatencyMs,
                        RelayName = p.RelayName,
                        AddressBytes = p.AddressBytes
                    });
                }
            }

            return hints;
        }
    }
}
