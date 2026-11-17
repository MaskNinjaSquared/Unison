// =============================================================================
// RelayMediaConfigMapper
//
// Core CallMediaHints → Voip RelayMediaConfig (Voip must not reference Core).
// =============================================================================
using Unison.Core.Models;
using Unison.Voip.Engine.Relay;

namespace Unison.WhatsAppClient.Calls
{
    public static class RelayMediaConfigMapper
    {
        /// <summary>
        /// The relay-elected peer (relay <c>peer_jid</c>) wins over the signaling peer:
        /// its LID keys the inbound RTP.
        /// </summary>
        public static RelayMediaConfig From(
            CallMediaHints hints,
            string callId,
            string selfLid,
            string signalingPeerLid,
            bool inbound)
        {
            if (hints == null)
            {
                return null;
            }

            var config = new RelayMediaConfig
            {
                CallId = callId,
                SelfLid = selfLid,
                PeerLid = string.IsNullOrWhiteSpace(hints.RelayPeerJid) ? signalingPeerLid : hints.RelayPeerJid,
                Inbound = inbound,
                CallKey = hints.CallKey,
                RelayKey = hints.RelayKey
            };

            if (hints.RelayTokens != null)
            {
                config.RelayTokens.AddRange(hints.RelayTokens);
            }

            if (hints.RelayEndpoints != null)
            {
                foreach (CallMediaRelayEndpoint e in hints.RelayEndpoints)
                {
                    config.Endpoints.Add(new RelayMediaEndpoint
                    {
                        RelayName = e.RelayName,
                        TokenId = e.TokenId,
                        AuthTokenId = e.AuthTokenId,
                        IsFna = e.IsFna,
                        Ipv4 = e.Ipv4,
                        Port = e.Port
                    });
                }
            }

            return config;
        }
    }
}
