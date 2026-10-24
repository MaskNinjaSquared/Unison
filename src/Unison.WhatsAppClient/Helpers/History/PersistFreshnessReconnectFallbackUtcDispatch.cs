// =============================================================================
// PersistFreshnessReconnectFallbackUtcDispatch
//
// Body of PersistFreshnessReconnectFallbackUtc. Local settings I/O on the host.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class PersistFreshnessReconnectFallbackUtcDispatch
    {
        public static void Run(
            IPersistFreshnessReconnectFallbackUtcHost host,
            DateTime timestampUtc)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            host.SetLastFreshnessReconnectFallbackUtc(timestampUtc);
            try
            {
                host.WriteLastFreshnessReconnectFallbackUtc(timestampUtc.ToString("O"));
            }
            catch (Exception ex)
            {
                host.LogPersistFreshnessReconnectFallbackFailed(ex.Message);
            }
        }
    }
}
