// =============================================================================
// ForceHistoryRepairOwner
//
// Owns IForceHistoryRepairPendingHost + IForceHistoryRepairConsumeHost: the
// MessageStoreForceHistoryRepair latch in local settings, set when tips are
// still owed and consumed by the next session as a tracked FULL_HISTORY ask.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class ForceHistoryRepairOwner :
        IForceHistoryRepairPendingHost,
        IForceHistoryRepairConsumeHost
    {
        private readonly ILocalSettings _settings;
        private readonly Func<IHistoryService> _history;
        private readonly Func<string, bool, Task<bool>> _requestFullHistoryTrackedAsync;
        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly Action<string> _log;

        public ForceHistoryRepairOwner(
            ILocalSettings settings,
            Func<IHistoryService> history,
            Func<string, bool, Task<bool>> requestFullHistoryTrackedAsync,
            IRuntimeDiagnostics diagnostics,
            Action<string> log)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _history = history ?? throw new ArgumentNullException(nameof(history));
            _requestFullHistoryTrackedAsync = requestFullHistoryTrackedAsync
                ?? throw new ArgumentNullException(nameof(requestFullHistoryTrackedAsync));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public void MarkPending(string reason) => ForceHistoryRepairPendingDispatch.Run(this, reason);

        public Task ConsumeAsync(string reason) => ForceHistoryRepairConsumeDispatch.RunAsync(this, reason);

        void IForceHistoryRepairPendingHost.SetForceHistoryRepairPending() =>
            _settings.Set(LocalSettingsConstants.MessageStoreForceHistoryRepair, true);

        void IForceHistoryRepairPendingHost.LogForceHistoryRepairPending(string message) => _log(message);

        void IForceHistoryRepairPendingHost.WriteForceHistoryRepairLatched(string reason) =>
            _diagnostics.Write(
                "history",
                "force-history-repair-latched",
                "reason=" + (reason ?? string.Empty));

        bool IForceHistoryRepairConsumeHost.TryReadForceHistoryRepairPending() =>
            _settings.Get<bool>(LocalSettingsConstants.MessageStoreForceHistoryRepair);

        bool IForceHistoryRepairConsumeHost.IsPairingBootstrapActive =>
            _history()?.IsPairingBootstrapActive ?? false;

        void IForceHistoryRepairConsumeHost.LogForceHistoryRepair(string message) => _log(message);

        Task<bool> IForceHistoryRepairConsumeHost.RequestFullHistoryOnDemandTrackedAsync(
            string reason,
            bool isFreshnessRepair) =>
            _requestFullHistoryTrackedAsync(reason, isFreshnessRepair);

        void IForceHistoryRepairConsumeHost.ClearForceHistoryRepairPending() =>
            _settings.Set(LocalSettingsConstants.MessageStoreForceHistoryRepair, false);
    }
}
