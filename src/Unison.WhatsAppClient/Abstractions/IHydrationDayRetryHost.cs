// =============================================================================
// IHydrationDayRetryHost
//
// Side effects for HydrationDayRetryDispatch.
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IHydrationDayRetryHost
    {
        int HydrationDayAttempts { get; }

        int MaxHydrationDayAttempts { get; }

        TimeSpan HydrationDayRetryInterval { get; }

        bool TryGetFreshnessStaleReason(DateTime nowUtc, out string staleReason);

        CancellationTokenSource ReplaceHydrationDayRetryCts();

        void LogHydrationDayRetry(string message);

        void FireAndForgetHydrationDayRetry(Func<Task> work);

        Task DelayHydrationDayRetryAsync(TimeSpan delay, CancellationToken token);

        Task RunHydrationFallbackPipelineAsync(string reason);
    }
}
