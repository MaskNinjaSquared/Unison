using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService
    {
        // --- IPostReplayMaintenanceHost --------------------------------------

        TimeSpan IPostReplayMaintenanceHost.PostReplayQuietWait =>
            _postReplayMaintenance.QuietWait;

        bool IPostReplayMaintenanceHost.RunDelayedBulkAfterReplay(int offlineCount) =>
            _postReplayMaintenance.RunDelayedBulkAfterReplay(offlineCount);

        bool IPostReplayMaintenanceHost.IsConnected => IsConnected;

        bool IPostReplayMaintenanceHost.IsWindowVisible => IsAppWindowVisible;

        bool IPostReplayMaintenanceHost.IsHistoryCatchUpActive => IsHistoryCatchUpActive();

        bool IPostReplayMaintenanceHost.IsListPaintPending =>
            _listPaintGate != null && !_listPaintGate.IsReleased;

        Task<bool> IPostReplayMaintenanceHost.WaitForStartupQuietAsync(
            TimeSpan timeout,
            CancellationToken token) =>
            WaitForStartupQuietAsync(timeout, "post-replay", token);

        Task<bool> IPostReplayMaintenanceHost.WaitForMemoryHeadroomAsync(
            CancellationToken token) =>
            WaitForMemoryHeadroomAsync("post-replay", token);

        void IPostReplayMaintenanceHost.ClearSyncStatus() => RaiseSyncStatus(null);

        void IPostReplayMaintenanceHost.WriteMemorySkipped() =>
            _diagnostics.Write(
                "startup",
                "post-replay-maintenance-skipped",
                "reason=memory; level=" + _appMemory.UsageLevel);

        void IPostReplayMaintenanceHost.MarkEnrichmentAwaitingHeavyHistory() =>
            _heavyHistoryEnrichment.SetAwaitingHeavyHistory(true);

        void IPostReplayMaintenanceHost.WriteDeferred(
            string catchUpDeferReason,
            int offlineCount)
        {
            _diagnostics.Write(
                "startup",
                "post-replay-maintenance-deferred",
                "reason=" + catchUpDeferReason + "; offlineCount=" + offlineCount);
            Debug.WriteLine(
                "[WhatsAppService] Post-replay maintenance deferred (" +
                catchUpDeferReason + "; offlineCount=" + offlineCount + ")");
        }

        void IPostReplayMaintenanceHost.WriteListPaintDeferred() =>
            _diagnostics.Write(
                "preview-tip",
                "reconcile-deferred-list-paint",
                "reason=post-replay");

        async Task IPostReplayMaintenanceHost.NotifyPostReplayMaintenanceAsync(
            int offlineCount,
            bool runDelayedBulk,
            CancellationToken token)
        {
            if (ChatFacade == null)
            {
                return;
            }

            await ChatFacade.NotifyPostReplayMaintenanceAsync(
                    offlineCount,
                    runDelayedBulk,
                    token)
                .ConfigureAwait(false);
        }

        void IPostReplayMaintenanceHost.WriteComplete(int offlineCount) =>
            _diagnostics.Write(
                "startup",
                "post-replay-message-maintenance-complete",
                "offlineCount=" + offlineCount);

        void IPostReplayMaintenanceHost.TrySchedulePostMessageEnrichment(string reason) =>
            TrySchedulePostMessageEnrichment(reason);

        void IPostReplayMaintenanceHost.RecordFailed(Exception ex, int offlineCount) =>
            _diagnostics.RecordException(
                "startup",
                "post-replay-maintenance-failed",
                ex,
                "offlineCount=" + offlineCount);

        // --- IBackgroundResolutionGate ---------------------------------------

        Task<bool> IBackgroundResolutionGate.WaitForQuietAsync(
            TimeSpan budget,
            string reason,
            CancellationToken token) =>
            WaitForStartupQuietAsync(budget, reason, token);

        bool IBackgroundResolutionGate.IsHandshakeReady =>
            _socket != null && _socket.IsHandshakeComplete;

        bool IBackgroundResolutionGate.ShouldDeferReplay => ShouldDeferReconnectReplayWork();

        bool IBackgroundResolutionGate.IsHistoryCatchUpActive => IsHistoryCatchUpActive();

        bool IBackgroundResolutionGate.ShouldDeferAvatarFetch(out string reason) =>
            _syncWork.ShouldDeferAvatarFetch(out reason);

        void IBackgroundResolutionGate.MarkEnrichmentAwaitingHeavyHistory() =>
            _heavyHistoryEnrichment.SetAwaitingHeavyHistory(true);

        void IBackgroundResolutionGate.ClearSyncStatus() => RaiseSyncStatus(null);

        void IBackgroundResolutionGate.PublishClearedSyncStatus() => PublishSyncStatus(null);

        IDisposable IBackgroundResolutionGate.TracePhase(string phase) => TraceStartupPhase(phase);

        void IBackgroundResolutionGate.WriteDeferredCatchUp(string reason) =>
            _diagnostics.Write(
                "startup",
                "background-resolution-deferred",
                "reason=" + (reason ?? string.Empty));
    }
}