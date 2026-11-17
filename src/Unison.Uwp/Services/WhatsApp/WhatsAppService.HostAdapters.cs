using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Proto;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Unison.WhatsAppClient.Messages;

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

        void IPostReplayMaintenanceHost.ClearSyncStatus() => _syncStatus.Raise(null);

        void IPostReplayMaintenanceHost.WriteMemorySkipped() =>
            _diagnostics.Write(
                "startup",
                "post-replay-maintenance-skipped",
                "reason=memory; level=" + _appMemory.UsageLevel);

        void IPostReplayMaintenanceHost.MarkEnrichmentAwaitingHeavyHistory() =>
            _historyCatchUp.HeavyHistoryEnrichment.SetAwaitingHeavyHistory(true);

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
            PostMessageEnrichmentDispatch.TrySchedule(this, reason);

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
            _socketSlot.Current != null && _socketSlot.Current.IsHandshakeComplete;

        bool IBackgroundResolutionGate.ShouldDeferReplay => _syncWork.IsReplayDrainActive;

        bool IBackgroundResolutionGate.IsHistoryCatchUpActive => IsHistoryCatchUpActive();

        bool IBackgroundResolutionGate.ShouldDeferAvatarFetch(out string reason) =>
            _syncWork.ShouldDeferAvatarFetch(out reason);

        void IBackgroundResolutionGate.MarkEnrichmentAwaitingHeavyHistory() =>
            _historyCatchUp.HeavyHistoryEnrichment.SetAwaitingHeavyHistory(true);

        void IBackgroundResolutionGate.ClearSyncStatus() => _syncStatus.Raise(null);

        void IBackgroundResolutionGate.PublishClearedSyncStatus() => _syncStatus.Publish(null);

        IDisposable IBackgroundResolutionGate.TracePhase(string phase) => TraceStartupPhase(phase);

        void IBackgroundResolutionGate.WriteDeferredCatchUp(string reason) =>
            _diagnostics.Write(
                "startup",
                "background-resolution-deferred",
                "reason=" + (reason ?? string.Empty));

        // --- IPostSyncMaintenanceIoHost --------------------------------------

        Task IPostSyncMaintenanceIoHost.RunPostReplayMaintenanceAsync(
            int offlineCount,
            CancellationToken token) =>
                    // Messages first: keep the socket and UI free for live traffic and catch-up.
                    // Names / groups / avatars wait for SchedulePostMessageEnrichment.
            PostReplayMaintenanceDispatch.RunAsync(this, offlineCount, token);

        Task IPostSyncMaintenanceIoHost.RunPostMessageEnrichmentAsync(
            string reason,
            CancellationToken token) =>
            PostMessageEnrichmentDispatch.RunAsync(this, reason, token);

        void IPostSyncMaintenanceIoHost.ClearEnrichmentAwaitingHeavyHistory() =>
            _historyCatchUp.HeavyHistoryEnrichment.SetAwaitingHeavyHistory(false);

        void IPostSyncMaintenanceIoHost.LogPostMessageEnrichment(string message) =>
            Debug.WriteLine("[WhatsAppService] " + message);

        void IPostSyncMaintenanceIoHost.WritePostMessageEnrichmentScheduled(string reason) =>
            _diagnostics.Write(
                "startup",
                "post-message-enrichment-scheduled",
                "reason=" + reason);

        /// <summary>
        /// Names / groups / avatars after messages. Skips while a heavy history catch-up is owed
        /// or in flight so reconnect does not spend the socket on USync before the backlog.
        /// </summary>
        private void OnChatListInitialPaintReleased(object sender, EventArgs e)
        {
            if (Interlocked.Exchange(ref _enrichmentAwaitingListPaint, 0) != 1)
            {
                return;
            }

            PostMessageEnrichmentDispatch.TrySchedule(this, "list-paint-released");
        }

        bool IPostMessageEnrichmentHost.EnrichmentAwaitingHeavyHistory =>
            _historyCatchUp.HeavyHistoryEnrichment.IsAwaitingHeavyHistory;

        bool IPostMessageEnrichmentHost.IsHistoryCatchUpActive => IsHistoryCatchUpActive();

        bool IPostMessageEnrichmentHost.IsFullHistoryInFlight =>
            _historyRequests.IsFullInFlight;

        bool IPostMessageEnrichmentHost.IsListPaintPending =>
            _listPaintGate != null && !_listPaintGate.IsReleased;

        bool IPostMessageEnrichmentHost.IsConnected => IsConnected;

        bool IPostMessageEnrichmentHost.IsWindowVisible => IsAppWindowVisible;

        bool IPostMessageEnrichmentHost.SerializePostSyncEnrichment =>
            EnrichmentTuning.SerializePostSyncEnrichment;

        TimeSpan IPostMessageEnrichmentHost.EnrichmentQuietWait =>
            EnrichmentTuning.PostSyncEnrichmentQuietWait;

        void IPostMessageEnrichmentHost.LogDeferUntilHeavyFinishes(string reason) =>
                Debug.WriteLine(
                    "[WhatsAppService] Post-message enrichment deferred until heavy history finishes (" +
                    reason + ")");

        void IPostMessageEnrichmentHost.MarkAwaitingHeavyHistory() =>
            _historyCatchUp.HeavyHistoryEnrichment.SetAwaitingHeavyHistory(true);

        void IPostMessageEnrichmentHost.WriteCatchUpDeferred(
            string reason,
            string catchUpDeferReason)
        {
                Debug.WriteLine(
                    "[WhatsAppService] Post-message enrichment deferred (" + catchUpDeferReason +
                    "; " + reason + ")");
                _diagnostics.Write(
                    "startup",
                    "post-message-enrichment-deferred",
                    "reason=" + (reason ?? string.Empty) + "; " + catchUpDeferReason);
            }

        void IPostMessageEnrichmentHost.LogArmAfterFullHistory(string reason) =>
                Debug.WriteLine(
                    "[WhatsAppService] Post-message enrichment armed for after FULL_HISTORY (" +
                    reason + ")");

        void IPostMessageEnrichmentHost.MarkAwaitingListPaint() =>
                Interlocked.Exchange(ref _enrichmentAwaitingListPaint, 1);

        void IPostMessageEnrichmentHost.WriteListPaintDeferred(string reason)
        {
                Debug.WriteLine(
                    "[WhatsAppService] Post-message enrichment deferred until chat list paint (" +
                    reason + ")");
                    _diagnostics.Write(
                    "startup",
                    "post-message-enrichment-deferred-list-paint",
                    "reason=" + (reason ?? string.Empty));
                }

        void IPostMessageEnrichmentHost.BeginEnrichmentSchedule(string reason) =>
            _startupHost.ScheduleEnrichment(reason);

        Task<bool> IPostMessageEnrichmentHost.WaitForStartupQuietAsync(
            TimeSpan timeout,
            string phase,
            CancellationToken token) =>
            WaitForStartupQuietAsync(timeout, phase, token);

        void IPostMessageEnrichmentHost.ClearSyncStatus() => _syncStatus.Raise(null);

        async Task IPostMessageEnrichmentHost.ResolveMissingNamesAsync(CancellationToken token)
        {
            using (TraceStartupPhase("post-replay-names"))
            {
                // Serialized enrichment owns groups later (post-replay-groups). Embedding
                // QueryAllGroups(force) here held the names phase ~200s on Mobile Low.
                await (Contacts?.ResolveMissingNamesAsync(includeGroupListing: false)
                    ?? Task.CompletedTask)
                    .ConfigureAwait(false);
            }
        }

        async Task IPostMessageEnrichmentHost.RefreshContactNamesAsync(CancellationToken token)
        {
            using (TraceStartupPhase("post-replay-contacts"))
            {
                await (Contacts?.RefreshContactNamesAsync(includeGroups: false, force: false)
                    ?? Task.CompletedTask).ConfigureAwait(false);
            }
        }

        void IPostMessageEnrichmentHost.TriggerBackgroundResolution() =>
            Contacts?.TriggerBackgroundResolution();

        bool IPostMessageEnrichmentHost.TryDeferAvatarFetch(out string reason) =>
            _syncWork.ShouldDeferAvatarFetch(out reason);

        void IPostMessageEnrichmentHost.ScheduleDeferredAvatars(string reason) =>
            ScheduleDeferredAvatarsCore(reason, delay: null);

        async Task IPostMessageEnrichmentHost.RetrieveContactPicturesAsync(CancellationToken token)
        {
            IContactService contacts = Contacts;
            if (contacts == null)
            {
                    return;
                }

            using (TraceStartupPhase("post-replay-avatars"))
            {
                DeferredAvatars?.Cancel();
                await contacts.RetrieveContactPicturesAsync(token).ConfigureAwait(false);
            }
        }

        void IPostMessageEnrichmentHost.NotifyGroupListingDue() =>
            RaiseReport(
                () => GroupFacade?.NotifyGroupListingDue(),
                nameof(IGroupService.NotifyGroupListingDue));

        Task IPostMessageEnrichmentHost.ReconcileGroupCommunityTitlesAsync(CancellationToken token)
        {
            IGroupService groups = GroupFacade;
            if (groups == null)
            {
                return Task.CompletedTask;
            }

            return groups.ReconcileGroupCommunityTitlesAsync(token);
        }

        void IPostMessageEnrichmentHost.WriteComplete(string reason, bool serialized) =>
                    _diagnostics.Write(
                        "startup",
                        "post-message-enrichment-complete",
                "reason=" + reason + "; serial=" + serialized);

        void IPostMessageEnrichmentHost.RecordFailed(Exception ex, string reason) =>
                _diagnostics.RecordException(
                        "startup",
                        "post-message-enrichment-failed",
                        ex,
                        "reason=" + reason);

        /// <summary>
        /// Flush lock / idle timer / claim-drain owner (DI exposes
        /// <see cref="IPendingMessageFlushHost"/> from this instance).
        /// </summary>
        internal OfflineReplayFlush OfflineReplayFlush => _offlineReplayFlush;

        internal QrCodeWireOwner QrCodeWire => _qrCodeWire;

        internal AuthStateWireOwner AuthStateWire => _connectionSession.AuthStateWire;

        internal SessionInitializedOwner SessionInitializedWire => _connectionSession.SessionInitializedWire;

        internal SocketErrorOwner SocketErrorWire => _connectionSession.SocketErrorWire;

        internal ConnectionUpdateOwner ConnectionUpdateWire => _connectionSession.ConnectionUpdateWire;

        internal SocketFacadeWireOwner SocketFacadeWire => _socketFacadeWire;

        internal InboundWireNodeOwner InboundWireNode => _inboundWireNode;

        internal DecryptedMessageWireOwner DecryptedMessageWire => _decryptedMessageWire;

        internal HistorySyncWireOwner HistorySyncWire => _historySyncWire;

        internal OfflineReplayReleaseOwner OfflineReplayReleaseWire => _offlineReplayRelease;

        private Task ApplyHistorySyncFromWireAsync(HistorySync sync)
        {
            if (History == null)
            {
                throw new InvalidOperationException(
                    "IHistoryService must resolve before history sync apply.");
            }

            return History.ApplyHistorySyncAsync(sync);
        }

        private async Task FlushOfflineReplayFromWireAsync(string reason)
            {
                try
                {
                await _offlineReplayFlush.FlushOfflineReplayAsync(reason).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "[WhatsAppService] Non-fatal offline replay message flush failure after offline drain: " +
                    ex.Message);
            }
        }

        private async Task NotifyOfflineReplayMessagesReleasedAsync(
            int offlineCount,
            bool firstRelease,
            bool transportReady)
        {
            try
            {
                if (Messages != null)
                {
                    await Messages.NotifyOfflineReplayReleasedAsync(
                        offlineCount,
                        firstRelease,
                        transportReady).ConfigureAwait(false);
                }
                else
                {
                    Debug.WriteLine(
                        "[WhatsAppService] Offline settle skipped: MessageFacade not attached.");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "[WhatsAppService] Non-fatal offline replay settle failure: " + ex.Message);
            }
        }

    }
}