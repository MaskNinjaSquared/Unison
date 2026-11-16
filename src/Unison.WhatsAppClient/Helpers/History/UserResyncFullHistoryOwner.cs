// =============================================================================
// UserResyncFullHistoryOwner
//
// Owns IUserResyncFullHistoryHost. Reads the FULL_HISTORY gate on
// HistoryOnDemandRegistry; connect / tracked send / force-repair consume stay ports.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class UserResyncFullHistoryOwner : IUserResyncFullHistoryHost
    {
        private readonly HistoryOnDemandRegistry _historyRequests;
        private readonly Func<int, bool, Task> _ensureConnectedAsync;
        private readonly Func<string, bool, Task<bool>> _requestFullHistoryTrackedAsync;
        private readonly Func<string, Task> _consumeForceHistoryRepairAsync;
        private readonly Action<string> _log;
        private readonly Action<string> _raiseStatus;

        public UserResyncFullHistoryOwner(
            HistoryOnDemandRegistry historyRequests,
            Func<int, bool, Task> ensureConnectedAsync,
            Func<string, bool, Task<bool>> requestFullHistoryTrackedAsync,
            Func<string, Task> consumeForceHistoryRepairAsync,
            Action<string> log,
            Action<string> raiseStatus)
        {
            _historyRequests = historyRequests
                ?? throw new ArgumentNullException(nameof(historyRequests));
            _ensureConnectedAsync = ensureConnectedAsync
                ?? throw new ArgumentNullException(nameof(ensureConnectedAsync));
            _requestFullHistoryTrackedAsync = requestFullHistoryTrackedAsync
                ?? throw new ArgumentNullException(nameof(requestFullHistoryTrackedAsync));
            _consumeForceHistoryRepairAsync = consumeForceHistoryRepairAsync
                ?? throw new ArgumentNullException(nameof(consumeForceHistoryRepairAsync));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _raiseStatus = raiseStatus ?? throw new ArgumentNullException(nameof(raiseStatus));
        }

        public Task<bool> RunAsync(string reason) =>
            UserResyncFullHistoryDispatch.RunAsync(this, reason);

        Task IUserResyncFullHistoryHost.EnsureConnectedAsync(int timeoutMs, bool forceFreshTransport) =>
            _ensureConnectedAsync(timeoutMs, forceFreshTransport);

        Task<bool> IUserResyncFullHistoryHost.RequestFullHistoryOnDemandTrackedAsync(
            string reason,
            bool isFreshnessRepair) =>
            _requestFullHistoryTrackedAsync(reason, isFreshnessRepair);

        void IUserResyncFullHistoryHost.ReleaseFullHistoryRequestGate() =>
            _historyRequests.ReleaseFull();

        bool IUserResyncFullHistoryHost.IsFullHistoryClaimed => _historyRequests.IsFullClaimed;

        Task IUserResyncFullHistoryHost.TryConsumeMessageStoreForceHistoryRepairAsync(string reason) =>
            _consumeForceHistoryRepairAsync(reason);

        void IUserResyncFullHistoryHost.LogUserResync(string message) => _log(message);

        void IUserResyncFullHistoryHost.RaiseUserResyncStatus(string status) =>
            _raiseStatus(status);
    }
}
