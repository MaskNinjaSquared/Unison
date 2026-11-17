// =============================================================================
// RelayMediaEndpointSelector
//
// Same pick as meowcaller getMediaRelayEndpoint / Socket CallRelayParser.
// =============================================================================
using System.Collections.Generic;

namespace Unison.Voip.Engine.Relay
{
    public static class RelayMediaEndpointSelector
    {
        public static RelayMediaEndpoint Select(IList<RelayMediaEndpoint> endpoints, bool inbound)
        {
            if (endpoints == null || endpoints.Count == 0)
            {
                return null;
            }

            if (inbound)
            {
                for (int i = 0; i < endpoints.Count; i++)
                {
                    if (endpoints[i].IsFna)
                    {
                        return endpoints[i];
                    }
                }
            }

            for (int i = 0; i < endpoints.Count; i++)
            {
                if (!endpoints[i].IsFna && endpoints[i].AuthTokenId != 0)
                {
                    return endpoints[i];
                }
            }

            for (int i = 0; i < endpoints.Count; i++)
            {
                if (!endpoints[i].IsFna)
                {
                    return endpoints[i];
                }
            }

            return endpoints[0];
        }
    }
}
