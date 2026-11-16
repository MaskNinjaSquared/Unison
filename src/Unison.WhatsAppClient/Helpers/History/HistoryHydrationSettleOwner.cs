// =============================================================================
// HistoryHydrationSettleOwner
//
// Owns IWaitForHistoryHydrationSettleHost: waits (bounded) for in-flight
// per-chat ON_DEMAND requests to land before the next hydration step.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class HistoryHydrationSettleOwner : IWaitForHistoryHydrationSettleHost
    {
        private readonly HistoryOnDemandRegistry _historyRequests;
        private readonly Func<bool> _isConnected;
        private readonly TimeSpan _settleBudget;

        public HistoryHydrationSettleOwner(
            HistoryOnDemandRegistry historyRequests,
            Func<bool> isConnected,
            TimeSpan settleBudget)
        {
            _historyRequests = historyRequests
                ?? throw new ArgumentNullException(nameof(historyRequests));
            _isConnected = isConnected ?? throw new ArgumentNullException(nameof(isConnected));
            _settleBudget = settleBudget;
        }

        public Task WaitAsync() => WaitForHistoryHydrationSettleDispatch.RunAsync(this);

        TimeSpan IWaitForHistoryHydrationSettleHost.HydrationSettleBudget => _settleBudget;

        bool IWaitForHistoryHydrationSettleHost.IsConnected => _isConnected();

        int IWaitForHistoryHydrationSettleHost.HistoryChatsInFlight => _historyRequests.ChatsInFlight;

        Task IWaitForHistoryHydrationSettleHost.DelayAsync(TimeSpan delay) => Task.Delay(delay);
    }
}
