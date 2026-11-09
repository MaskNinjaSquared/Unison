using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Factories;
using Unison.Core.Helpers;
using Unison.Core.Helpers.Chats;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Core.ViewModels
{
    public partial class ChatListViewModel
    {
        // ---------------------------------------------------------------------
        // Lifecycle
        // ---------------------------------------------------------------------

        /// <summary>
        /// Starts listening and renders what is already there. Idempotent, and paired with
        /// <see cref="Detach"/>: this view model is created per screen while the facades it
        /// listens to live as long as the app, so a screen that leaves without detaching is
        /// never collected.
        /// </summary>
        public void Attach()
        {
            if (_attached) return;
            _attached = true;
            _visiblePaintGeneration = ChatListVisiblePaintOwner.Claim(this);

            _connection.StatusChanged += Connection_StatusChanged;
            _history.SyncStatusChanged += History_SyncStatusChanged;
            _history.HistorySyncReceived += History_SyncReceived;
            _history.InitialSyncProgress += History_InitialSyncProgress;
            _history.ChatPreviewChunkPersisted += History_ChatPreviewChunkPersisted;
            _history.HistoryMessageChunkPersisted += History_MessageChunkPersisted;
            _contactService.DisplayNamesUpdated += Contacts_DisplayNamesUpdated;
            _chatState.CommunitySubjectsUpdated += ChatState_CommunitySubjectsUpdated;
            _chatState.BulkCatalogueMutationEnded += ChatState_BulkCatalogueMutationEnded;
            _chatService.VisibleOrderInvalidated += ChatService_VisibleOrderInvalidated;
            _chatState.Chats.CollectionChanged += ChatState_ChatsChanged;

            foreach (var chat in _chatState.Chats)
            {
                if (chat == null) continue;
                chat.PropertyChanged -= ChatItem_PropertyChanged;
                chat.PropertyChanged += ChatItem_PropertyChanged;
            }

            if (_deepLinkHold != null)
            {
                _deepLinkHold.Changed += DeepLinkHold_Changed;
            }

            if (_timelinePriority != null)
            {
                _timelinePriority.Changed += TimelinePriority_Changed;
            }

            if (_appVisibility != null)
            {
                _appVisibility.Changed += AppVisibility_Changed;
            }

            // Scope-filtered badge: Channels/Archived show their own unread count, not Active's.
            _notificationCounter = new NotificationCounterWatch(
                _chatState.Chats,
                MatchesScope);
            _notificationCounter.Changed += NotificationCounter_Changed;
            RaiseProperties(nameof(NotificationCounterBadge), nameof(NotificationCounterBadgeText));

            IsLoadingOverlayVisible = _chatState.Chats.Count == 0;

            TraceListLifecycle("attach", BuildListSnapshot());
            RaiseChatListMaterialized();

            // First history sync / cold toast: keep the list unmaterialized. Deep-link
            // hold shows only ChatList_Loading until the conversation is ready â€” sync
            // banner is deferred and flushed on release.
            if (IsListHeldForSync)
            {
                if (IsDeepLinkHeldPreferLoading)
                {
                    ShowDeepLinkLoadingOverlay();
                }
                else
                {
                    ShowSyncProgressOnly(
                        _history.InitialSyncProcessedConversations,
                        _history.InitialSyncTotalConversations);
                }
            }
            else if (IsInSafeMode)
            {
                // Resume/reattach while a trailing empty chunk re-armed safe mode — the list
                // is already painted; do not restart BeginBatch (release-tick hang).
                if (IsChatListMaterialized)
                {
                    NotifyListPaintReleased("attach-safe-mode-already-materialized");
                }
                else
                {
                    BeginBatchRendering(
                        _history.InitialSyncProcessedConversations,
                        _history.InitialSyncTotalConversations,
                        _chatState.Chats.Count);
                }
            }
            else
            {
                NotifyListPaintReleased("attach-not-held");
            }

            _ = PresentFromConnectionStatusAsync(_connection.CurrentStatus);

            // ChatStore warm is fire-and-forget at DI attach; wait then re-apply pins so a cold
            // list does not look unpinned until the user opens a chat.
            _ = EnsureLocalChatPinsAppliedAsync();

            if (!_batchRendering)
            {
                // Transient VMs miss SyncStatusChanged history — seed from the shared channel
                // so Active+frugal still defers mid-groups (aefa3dfb Channels attach was fine
                // to run; Active cold rebuild is what hung the UI for minutes).
                if (string.IsNullOrWhiteSpace(_lastSyncPhaseToken) && _syncStatus != null)
                {
                    _lastSyncPhaseToken = _syncStatus.LastEnrichmentStatus;
                }

                // Frugal Active only: Channels/Archived use the lean catalog path and paint now.
                if (ChatListEnrichmentRefresh.ShouldDeferVisibleRefresh(
                        PreferFrugalUi,
                        _chatService != null &&
                        _chatService.MatchesListEnrichmentPhase(_lastSyncPhaseToken),
                        _scope))
                {
                    _refreshDeferredForEnrichment = true;
                    _visibleOrderDirty = true;
                    EnsureDeferredEmptyListLoadingOverlay();
                    // Keep the freio for mid-enrichment storms, but paint once when unload
                    // left VisibleChats empty — otherwise Groups/Avatars stuck without
                    // Raise(null) leave ChatList_Loading forever (f7af47e0).
                    if (ChatListEnrichmentRefresh.ShouldPaintEmptyListDuringEnrichmentDeferral(
                            VisibleChats.Count))
                    {
                        ScheduleRefreshVisibleChats(
                            ChatListEnrichmentRefresh.EmptyDeferredListPaintDelay,
                            allowDuringEnrichment: true);
                    }

                    TraceListLifecycle(
                        "refresh-deferred-enrichment-attach",
                        BuildListSnapshot("visible=" + VisibleChats.Count),
                        flush: true);
                }
                else
                {
                    _ = _dispatcher.RunAsync(() =>
                    {
                        if (!_attached || _batchRendering || IsInSafeMode || IsListHeldForSync)
                        {
                            return;
                        }

                        RefreshVisibleChats();
                    });
                }
            }
        }

        /// <summary>
        /// ChatStore.WarmAsync races the first preview hydrate. After warm completes, re-apply
        /// pin/mute and re-sort so pinned rows are not stuck mid-list until a chat is opened.
        /// </summary>
        private async Task EnsureLocalChatPinsAppliedAsync()
        {
            if (_chatStore == null)
            {
                return;
            }

            try
            {
                await _chatStore.WarmAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[ChatListViewModel] ChatStore warm (list attach) failed: " + ex.Message);
                return;
            }

            if (!_attached)
            {
                return;
            }

            await _dispatcher.RunAsync(() =>
            {
                if (!_attached)
                {
                    return;
                }

                ApplyLocalChatFieldsToScopedChats();
                if (_batchRendering)
                {
                    ReorderVisibleChatsInPlace();
                    return;
                }

                // Cold populate / in-flight refresh already owns the UI turn. Nesting
                // RefreshVisibleChats here cancelled Continues and rebuilt source twice
                // (Mobile Low: ~500ms + ~500ms on attach, then later enrichment rebuilds).
                // Keep _pendingVisibleRefreshSource — pin ApplyTo mutates the same ChatItem
                // refs already in that list; nulling forced Continue to rebuild (~500ms).
                if (_visibleColdPopulate || IsRefreshing)
                {
                    _visibleOrderDirty = true;
                    return;
                }

                // Settings go-back / list unload mid-groups: Attach already deferred the
                // cold Active rebuild. Pins must not bypass that freio (c2ba3c60: ~285s
                // buildMs inside EnsureLocalChatPinsAppliedAsync while enrichment ran).
                bool enrichmentPhase = _chatService != null &&
                    _chatService.MatchesListEnrichmentPhase(_lastSyncPhaseToken);
                if (ChatListEnrichmentRefresh.ShouldDeferPinsVisibleRefresh(
                        _refreshDeferredForEnrichment,
                        PreferFrugalUi,
                        enrichmentPhase,
                        _scope))
                {
                    _refreshDeferredForEnrichment = true;
                    _visibleOrderDirty = true;
                    EnsureDeferredEmptyListLoadingOverlay();
                    // Attach may already have armed the empty paint; coalesce keeps it.
                    // If only pins saw the empty deferral, arm the same one-shot paint.
                    if (ChatListEnrichmentRefresh.ShouldPaintEmptyListDuringEnrichmentDeferral(
                            VisibleChats.Count))
                    {
                        ScheduleRefreshVisibleChats(
                            ChatListEnrichmentRefresh.EmptyDeferredListPaintDelay,
                            allowDuringEnrichment: true,
                            coalesceIfArmed: true);
                    }

                    TraceListLifecycle(
                        "refresh-deferred-enrichment-pins",
                        BuildListSnapshot("visible=" + VisibleChats.Count),
                        flush: true);
                    return;
                }

                // Pin ApplyTo already mutated ChatItem refs — reshuffle painted rows.
                // Full RefreshVisibleChats only when unload left the list empty.
                if (ChatListEnrichmentRefresh.PreferInPlaceReorderAfterPins(VisibleChats.Count))
                {
                    ReorderVisibleChatsInPlace();
                    return;
                }

                RefreshVisibleChats();
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Go-back mid-enrichment: store may already have chats (Attach would hide the
        /// cold-start overlay) while VisibleChats is still empty after unload.
        /// </summary>
        private void EnsureDeferredEmptyListLoadingOverlay()
        {
            if (!ChatListEnrichmentRefresh.ShouldShowLoadingWhileDeferredEmpty(
                    VisibleChats.Count,
                    _refreshDeferredForEnrichment))
            {
                return;
            }

            ShowDeepLinkLoadingOverlay();
        }

        /// <summary>
        /// ListView Unloaded runs before page Detach. Drop painted rows so UWP does not
        /// tear down hundreds of realized containers on the UI thread during resume /
        /// navigation (Mobile hang with active=none after ClearRecordingUi).
        /// </summary>
        public void PrepareListUnload()
        {
            _refreshCts?.Cancel();
            _refreshContinueCts?.Cancel();
            _pendingVisibleRefreshSource = null;
            if (_visibleColdPopulate)
            {
                _visibleColdPopulate = false;
                _coldPopulateGate?.Release();
            }

            ReleaseBatchColdPopulateGate();

            if (!ChatListUnloadPolicy.ShouldClearVisibleChats(VisibleChats.Count))
            {
                return;
            }

            IsRefreshing = true;
            try
            {
                VisibleChats.Clear();
            }
            finally
            {
                IsRefreshing = false;
            }

            _visibleOrderDirty = false;
            TraceListLifecycle(
                "list-unload-cleared",
                BuildListSnapshot("reason=listview-unloaded"),
                flush: true);
        }

        /// <summary>Mirror of <see cref="Attach"/>.</summary>
        public void Detach()
        {
            if (!_attached) return;
            _attached = false;
            PrepareListUnload();
            ChatListVisiblePaintOwner.Release(this, _visiblePaintGeneration);
            _visiblePaintGeneration = 0;

            CancellationTokenSource running;
            lock (_messagePreviewReconcileGate)
            {
                running = _messagePreviewReconcileCts;
                _messagePreviewReconcileCts = null;
            }

            CancelAndDispose(running);

            _connection.StatusChanged -= Connection_StatusChanged;
            _history.SyncStatusChanged -= History_SyncStatusChanged;
            _history.HistorySyncReceived -= History_SyncReceived;
            _history.InitialSyncProgress -= History_InitialSyncProgress;
            _history.ChatPreviewChunkPersisted -= History_ChatPreviewChunkPersisted;
            _history.HistoryMessageChunkPersisted -= History_MessageChunkPersisted;
            _contactService.DisplayNamesUpdated -= Contacts_DisplayNamesUpdated;
            _chatState.CommunitySubjectsUpdated -= ChatState_CommunitySubjectsUpdated;
            _chatState.BulkCatalogueMutationEnded -= ChatState_BulkCatalogueMutationEnded;
            _chatService.VisibleOrderInvalidated -= ChatService_VisibleOrderInvalidated;
            _chatState.Chats.CollectionChanged -= ChatState_ChatsChanged;

            if (_deepLinkHold != null)
            {
                _deepLinkHold.Changed -= DeepLinkHold_Changed;
            }

            if (_timelinePriority != null)
            {
                _timelinePriority.Changed -= TimelinePriority_Changed;
            }

            if (_appVisibility != null)
            {
                _appVisibility.Changed -= AppVisibility_Changed;
            }

            foreach (var chat in _chatState.Chats)
            {
                if (chat != null) chat.PropertyChanged -= ChatItem_PropertyChanged;
            }

            if (_notificationCounter != null)
            {
                _notificationCounter.Changed -= NotificationCounter_Changed;
                _notificationCounter.Dispose();
                _notificationCounter = null;
            }

            _refreshCts?.Cancel();
            _refreshContinueCts?.Cancel();
            _pendingVisibleRefreshSource = null;
            if (_visibleColdPopulate)
            {
                _visibleColdPopulate = false;
                _coldPopulateGate?.Release();
            }

            ReleaseBatchColdPopulateGate();
            lock (_previewHydrateGate)
            {
                _previewHydrateQueue?.Clear();
                _previewHydrateLoopRunning = false;
            }

            CloseCommunityHub();
        }

    }
}
