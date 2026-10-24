// =============================================================================
// IPeriodicFullHistorySafetyLoopHost
//
// Side effects for PeriodicFullHistorySafetyLoopDispatch.
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IPeriodicFullHistorySafetyLoopHost
    {
        TimeSpan PeriodicFullHistorySafetyInterval { get; }

        Task DelayAsync(TimeSpan delay, CancellationToken token);

        Task RunPeriodicFullHistorySafetyTickAsync();

        void LogPeriodicFullHistorySafetyTickFailed(string message);
    }
}
