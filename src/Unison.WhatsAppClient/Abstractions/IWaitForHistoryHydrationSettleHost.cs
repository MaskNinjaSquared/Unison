// =============================================================================
// IWaitForHistoryHydrationSettleHost
//
// Side effects for WaitForHistoryHydrationSettleDispatch.
// =============================================================================
using System;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IWaitForHistoryHydrationSettleHost
    {
        TimeSpan HydrationSettleBudget { get; }

        bool IsConnected { get; }

        int HistoryChatsInFlight { get; }

        Task DelayAsync(TimeSpan delay);
    }
}
