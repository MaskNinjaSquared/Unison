// =============================================================================
// IHydrationDayRetryIoHost
//
// Freshness / pipeline / stamp side effects for HydrationDayRetryOwner.
// CTS, day attempts and day anchor live on the owner.
// =============================================================================
using System;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IHydrationDayRetryIoHost
    {
        bool TryGetFreshnessStaleReason(DateTime nowUtc, out string staleReason);

        Task RunHydrationFallbackPipelineAsync(string reason);

        void StampLastHydrationFallbackUtc(DateTime nowUtc);

        void LogHydrationDayRetry(string message);
    }
}
