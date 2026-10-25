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

            _connection.StatusChanged += Connection_StatusChanged;
            _history.SyncStatusChanged += History_SyncStatusChanged;
            _history.HistorySyncReceived += History_SyncReceived;
            _history.InitialSyncProgress += History_InitialSyncProgress;
            _history.ChatPreviewChunkPersisted += History_ChatPreviewChunkPersisted;
            _history.HistoryMessageChunkPersisted += History_MessageChunkPersisted;
            _contactService.DisplayNamesUpdated += Contacts_DisplayNamesUpdated;
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

            _notificationCounter = new NotificationCounterWatch(_chatState.Chats);
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
                BeginBatchRendering(
                    _history.InitialSyncProcessedConversations,
                    _history.InitialSyncTotalConversations,
                    _chatState.Chats.Count);
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
                RefreshVisibleChats();
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
                }
                else
                {
                    RefreshVisibleChats();
                }
            }).ConfigureAwait(false);
        }

        /// <summary>Mirror of <see cref="Attach"/>.</summary>
        public void Detach()
        {
            if (!_attached) return;
            _attached = false;

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

            _batchRendering = false;
            lock (_previewHydrateGate)
            {
                _previewHydrateQueue?.Clear();
                _previewHydrateLoopRunning = false;
            }

            CloseCommunityHub();
        }

    }
}
