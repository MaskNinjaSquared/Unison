// =============================================================================
// IPersistFreshnessReconnectFallbackUtcHost
//
// Side effects for PersistFreshnessReconnectFallbackUtcDispatch.
// =============================================================================
using System;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IPersistFreshnessReconnectFallbackUtcHost
    {
        void SetLastFreshnessReconnectFallbackUtc(DateTime timestampUtc);

        void WriteLastFreshnessReconnectFallbackUtc(string roundtripText);

        void LogPersistFreshnessReconnectFallbackFailed(string message);
    }
}
