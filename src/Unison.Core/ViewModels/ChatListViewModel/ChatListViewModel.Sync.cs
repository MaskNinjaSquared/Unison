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
        // Status text
        // ---------------------------------------------------------------------

        /// <summary>
        /// Shared presentation: header UI when <see cref="DisplaySync"/>;
        /// StatusBar progress only when the Unison Mobile strategy owns it.
        /// </summary>
        private async Task PresentSyncStatusAsync(string message, bool visible, string source = null)
        {
            if (!string.IsNullOrEmpty(source))
            {
                LogSyncBanner(source, message, visible);
            }

            // The service already drops the text when the setting is off. This covers the
            // list's own status lines, so a hidden setting never becomes a visible row.
            if (_localSettings != null &&
                !_localSettings.Get<bool>(LocalSettingsConstants.ShowSyncStatus))
            {
                message = null;
                visible = false;
            }

            // Toast/tile: conversation first â€” stash sync line and keep Loading until release.
            if (_chatService.ShouldSuppressDeepLinkSyncBanner(IsDeepLinkHeld))
            {
                _pendingDeepLinkSyncMessage = message;
                _pendingDeepLinkSyncVisible = visible && !string.IsNullOrEmpty(message);
                SyncStatusText = string.Empty;
                IsSyncStatusVisible = false;
                if (_statusBar != null)
                {
                    await _statusBar.HideProgressAsync();
                }

                if (IsDeepLinkHeldPreferLoading)
                {
                    ShowDeepLinkLoadingOverlay();
                }

                return;
            }

            SyncStatusText = message ?? string.Empty;

            if (DisplaySync)
            {
                IsSyncStatusVisible = visible && !string.IsNullOrEmpty(message);
                if (_statusBar != null)
                {
                    await _statusBar.HideProgressAsync();
                }
                return;
            }

            IsSyncStatusVisible = false;
            if (_statusBar == null)
            {
                return;
            }

            if (visible && !string.IsNullOrEmpty(message))
            {
                await _statusBar.ShowProgressAsync(message);
            }
            else
            {
                await _statusBar.HideProgressAsync();
            }
        }

        private async Task PresentFromConnectionStatusAsync(string status)
        {
            if (_batchRendering)
            {
                UpdateBatchProgressText();
                return;
            }

            switch (status)
            {
                case "connecting":
                    await PresentSyncStatusAsync(
                        _strings.Get("ChatList_Connecting"),
                        visible: true,
                        source: "IConnectionService:" + status);
                    break;
                case "connected":
                    await PresentSyncStatusAsync(
                        _strings.Get("ChatList_Handshake"),
                        visible: true,
                        source: "IConnectionService:" + status);
                    break;
                case "open":
                    // Stays until IConnectionService publishes "synced" (offline drain).
                    await PresentSyncStatusAsync(
                        _strings.Get("ChatList_Updating"),
                        visible: true,
                        source: "IConnectionService:" + status + " until=synced");
                    break;
                case "close":
                case "synced":
                    await PresentSyncStatusAsync(
                        null,
                        visible: false,
                        source: "IConnectionService:" + status);
                    break;
                default:
                    if (!string.IsNullOrEmpty(status))
                    {
                        await PresentSyncStatusAsync(
                            status,
                            visible: true,
                            source: "IConnectionService:" + status);
                    }
                    break;
            }
        }

        private void LogSyncBanner(string source, string message, bool visible)
        {
            string ui = visible && !string.IsNullOrEmpty(message) ? message : "<hidden>";
            string line = "facade=" + (source ?? "internal") +
                " ui=" + ui +
                " visible=" + (visible && !string.IsNullOrEmpty(message));
            if (string.Equals(_lastSyncBannerLog, line, StringComparison.Ordinal))
            {
                return;
            }

            _lastSyncBannerLog = line;
            Debug.WriteLine("[ChatList/Sync] " + line);
            _sessionLogger?.WriteAlways("[ChatList/Sync] " + line);
            _diagnostics?.Write("chat-list", "sync-banner", line);
        }

        /// <summary>
        /// Crash-window breadcrumb: write + flush so Mobile can keep the last steps if the
        /// process is killed after the list materializes.
        /// </summary>
        private void TraceListLifecycle(string eventName, string details = null, bool flush = true)
        {
            if (_diagnostics == null)
            {
                return;
            }

            try
            {
                string mem = "gcMb=" + (GC.GetTotalMemory(false) / (1024 * 1024));
                string line = string.IsNullOrWhiteSpace(details) ? mem : details + "; " + mem;
                _diagnostics.Write("chat-list-lifecycle", eventName, line);
                if (flush)
                {
                    _ = _diagnostics.FlushAsync("chat-list:" + eventName);
                }
            }
            catch
            {
                // Diagnostics must never interfere with the list.
            }
        }

        private string BuildListSnapshot(string extra = null)
        {
            string line =
                "held=" + IsListHeldForSync +
                "; safe=" + IsInSafeMode +
                "; materialized=" + IsChatListMaterialized +
                "; batch=" + _batchRendering +
                "; batchDone=" + _batchCompleted +
                "; visible=" + VisibleChats.Count +
                "; store=" + (_chatState?.Chats?.Count ?? 0) +
                "; target=" + _batchVisibleTarget +
                "; processed=" + _batchProcessed +
                "; total=" + _batchTotal +
                "; hydratePending=" + HasPendingPreviewHydrate();
            return string.IsNullOrWhiteSpace(extra) ? line : line + "; " + extra;
        }

        private void ShowLoadingOverlay(string text)
        {
            LoadingOverlayText = text;
            IsLoadingOverlayVisible = true;
        }

        // ---------------------------------------------------------------------
        // Facade events
        // ---------------------------------------------------------------------

        private async void Connection_StatusChanged(object sender, string status)
        {
            await _dispatcher.RunAsync(() =>
            {
                if (_awaitingResyncHistory)
                {
                    // Still show reconnecting / syncing states while waiting for history.
                    if (string.Equals(status, "reconnecting", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(status, "connecting", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(status, "open", StringComparison.OrdinalIgnoreCase))
                    {
                        _ = PresentFromConnectionStatusAsync(status);
                    }

                    return;
                }

                _ = PresentFromConnectionStatusAsync(status);
            });
        }

        private async void History_SyncStatusChanged(object sender, string message)
        {
            string resolved = TranslateSyncPhase(message);

            await _dispatcher.RunAsync(() =>
            {
                _lastSyncPhaseToken = message;

                if (_batchRendering)
                {
                    UpdateBatchProgressText();
                    return;
                }

                if (_awaitingResyncHistory)
                {
                    // Surface service progress ("Re-syncing...", "Preparing...") instead of
                    // freezing on the wipe banner until the wait completes. The service's own
                    // "Saving chats..." is skipped: it is less specific than what we already show.
                    if (string.IsNullOrEmpty(resolved) ||
                        string.Equals(message, "Saving chats...", StringComparison.Ordinal))
                    {
                        return;
                    }

                    ShowLoadingOverlay(resolved);
                    _ = PresentSyncStatusAsync(resolved, visible: true, source: "IHistoryService:SyncStatus");
                    return;
                }

                if (!string.IsNullOrEmpty(resolved))
                    _ = PresentSyncStatusAsync(resolved, visible: true, source: "IHistoryService:SyncStatus");
                else
                    _ = PresentSyncStatusAsync(null, visible: false, source: "IHistoryService:SyncStatus");

                // One coalesced reshuffle after names/avatars/groups stop fighting the dispatcher.
                if (_refreshDeferredForEnrichment && !ListEnrichmentPhase.Matches(message))
                {
                    _refreshDeferredForEnrichment = false;
                    ScheduleRefreshVisibleChats();
                }
            });
        }

        /// <summary>
        /// Turns a <see cref="SyncPhaseStatus"/> token into localized text. Non-token messages are
        /// returned as they came, so the literal wording still in the service keeps working.
        /// </summary>
        private string TranslateSyncPhase(string message)
        {
            string phase;
            int current;
            int total;
            if (!SyncPhaseStatus.TryParse(message, out phase, out current, out total))
            {
                return message;
            }

            switch (phase)
            {
                case SyncPhaseStatus.Settling:
                    return GetPhaseString("ChatList_Settling", "Finishing startup...");
                case SyncPhaseStatus.LowMemory:
                    return GetPhaseString("ChatList_PausedLowMemory", "Paused - low memory");
                case SyncPhaseStatus.Names:
                    return FormatPhaseWithCount(
                        "ChatList_ResolvingNames", "Resolving names... {0} of {1}", current, total);
                case SyncPhaseStatus.Avatars:
                    return FormatPhaseWithCount(
                        "ChatList_FetchingAvatars", "Loading photos... {0} of {1}", current, total);
                case SyncPhaseStatus.Groups:
                    return FormatPhaseWithCount(
                        "ChatList_FetchingGroups", "Loading group info... {0} of {1}", current, total);
                case SyncPhaseStatus.HistoryCatchUp:
                    return GetPhaseString("ChatList_SyncingHistory", "Synchronizing historyâ€¦");
                default:
                    return null;
            }
        }

        /// <summary>
        /// A counted phase, or its bare form when the total is unknown / current is still zero â€”
        /// "0 of 40" reads worse than no number at all.
        /// </summary>
        private string FormatPhaseWithCount(string key, string fallback, int current, int total)
        {
            string format = GetPhaseString(key, fallback);
            if (total <= 0 || current <= 0)
            {
                int trim = format.IndexOf("{0}", StringComparison.Ordinal);
                return trim > 0 ? format.Substring(0, trim).TrimEnd() : format;
            }

            try
            {
                return string.Format(format, current, total);
            }
            catch (FormatException)
            {
                return format;
            }
        }

        private string GetPhaseString(string key, string fallback)
        {
            return _strings == null ? fallback : _strings.Get(key, fallback);
        }

        private async void History_SyncReceived(object sender, global::Proto.HistorySync sync)
        {
            await _dispatcher.RunAsync(() =>
            {
                if (_batchRendering)
                {
                    // The burst is over, but the rows already queued still have to be released at
                    // the same pace, so this only marks the end and lets the loop drain.
                    // SQLite path raises this with a null payload on every chunk while progress is
                    // still debouncing â€” do not force-complete or the banner vanishes mid-import.
                    if (sync == null && (IsInSafeMode || HasPendingPreviewHydrate()))
                    {
                        EnsureBatchLoop();
                        return;
                    }

                    _batchCompleted = true;
                    RaiseVisibleTarget(_chatState.Chats.Count);
                    EnsureBatchLoop();
                    return;
                }

                // SQLite path: progress events own the banner until quiet-finalize / hydrate drain.
                if (IsListHeldForSync || (sync == null && (IsInSafeMode || HasPendingPreviewHydrate())))
                {
                    return;
                }

                _ = PresentSyncStatusAsync(null, visible: false, source: "IHistoryService:HistorySyncReceived");
                IsLoadingOverlayVisible = false;
                RefreshVisibleChats();
            });
        }

        private async void History_InitialSyncProgress(object sender, InitialSyncProgressEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            await _dispatcher.RunAsync(() =>
            {
                TraceListLifecycle(
                    e.IsCompleted ? "initial-sync-completed-enter" : "initial-sync-progress",
                    BuildListSnapshot(
                        "active=" + e.IsActive +
                        "; completed=" + e.IsCompleted +
                        "; stage=" + (e.Stage ?? string.Empty) +
                        "; eProcessed=" + e.ProcessedConversations +
                        "; eTotal=" + e.TotalConversations +
                        "; eVisibleTarget=" + e.VisibleChatTarget),
                    flush: e.IsCompleted);

                // Safe mode gates the menu actions, and it flips with this event.
                RaiseMenuCommandsCanExecuteChanged();
                // HistoryFacade clears the first-sync flag before relaying a completed event.
                // This flips IsChatListMaterialized â†’ x:Load creates the ListView.
                RaiseChatListMaterialized();
                if (e.IsCompleted)
                {
                    TraceListLifecycle("list-materialized-after-completed", BuildListSnapshot());
                }

                if (!e.IsCompleted)
                {
                    if (IsListHeldForSync)
                    {
                        ShowSyncProgressOnly(e.ProcessedConversations, e.TotalConversations);
                    return;
                }

                    BeginBatchRendering(
                        e.ProcessedConversations,
                        e.TotalConversations,
                        e.VisibleChatTarget);
                    return;
                }

                // Persist quiet-finalize: release rows against the store we actually have â€”
                // e.VisibleChatTarget tracks sync conversation count (can be 400) and must not
                // become the UI release ceiling while hydrate is still filling the store.
                int storeCount = _chatState.Chats.Count;
                BeginBatchRendering(
                    e.ProcessedConversations,
                    e.TotalConversations,
                    Math.Max(BatchSize, storeCount));

                // SQLite previews may still be merging into the list after the chunk is marked
                // complete â€” keep the syncing banner until that queue drains.
                if (!HasPendingPreviewHydrate())
                {
                _batchCompleted = true;
                    TraceListLifecycle("batch-marked-complete-no-hydrate", BuildListSnapshot());
                    ScheduleFullSqlitePreviewReconcile();
                }
                else
                {
                    TraceListLifecycle("batch-wait-hydrate", BuildListSnapshot());
                }

                UpdateBatchProgressText();
                EnsureBatchLoop();
                TraceListLifecycle("initial-sync-completed-exit", BuildListSnapshot());
            });
        }

        /// <summary>
        /// Phase 2: SQLite list previews landed â€” enqueue and merge in small UI slices.
        /// </summary>
        private void History_ChatPreviewChunkPersisted(object sender, HistoryChatPreviewChunkEventArgs e)
        {
            if (e?.Rows == null || e.Rows.Count == 0)
            {
                return;
            }

            EnqueueHistoryChatPreviews(e.Rows);
        }

        /// <summary>
        /// Messages landed in SQLite (often without a matching preview apply) â€” reconcile
        /// LastMessage from the newest row so cross-device sends update the list strip.
        /// </summary>
        private void History_MessageChunkPersisted(object sender, HistoryMessageChunkEventArgs e)
        {
            if (e?.ChatJids == null || e.ChatJids.Count == 0 || _chatService == null)
            {
                return;
            }

            // Each per-chunk reconcile is a UI-thread pass plus a full re-sort of the store. The
            // completion event runs one full reconcile, so the first sync skips these.
            if (IsListHeldForSync)
            {
                return;
            }

            lock (_messagePreviewReconcileGate)
            {
                if (_messagePreviewReconcileQueue == null)
                {
                    _messagePreviewReconcileQueue = new List<string>(e.ChatJids.Count);
                }

                for (int i = 0; i < e.ChatJids.Count; i++)
                {
                    string jid = e.ChatJids[i];
                    if (!string.IsNullOrWhiteSpace(jid))
                    {
                        _messagePreviewReconcileQueue.Add(jid);
                    }
                }
            }

            _ = RunMessagePreviewReconcileAsync(BeginMessagePreviewReconcile(), full: false);
        }

        /// <summary>
        /// After sync quiet / preview hydrate drain: reconcile every visible chat from SQLite
        /// so Last Message matches history_message even when preview rows were stale.
        /// </summary>
        private void ScheduleFullSqlitePreviewReconcile()
        {
            if (_chatService == null)
            {
                return;
            }

            TraceListLifecycle("schedule-full-preview-reconcile", BuildListSnapshot(), flush: true);
            _ = RunMessagePreviewReconcileAsync(BeginMessagePreviewReconcile(), full: true);
        }

        /// <summary>
        /// Retires the running reconcile and returns the token of the one taking its place.
        /// </summary>
        /// <remarks>
        /// The swap happens under the same gate the queue uses, and the token comes from the
        /// local source rather than a second read of the field. Read twice without the gate,
        /// two reconciles starting together could each walk away holding the other's token, so
        /// cancelling one stopped the wrong worker and the one meant to stop kept writing
        /// previews over the fresher pass.
        /// </remarks>
        private CancellationToken BeginMessagePreviewReconcile()
        {
            var fresh = new CancellationTokenSource();
            CancellationTokenSource previous;

            lock (_messagePreviewReconcileGate)
            {
                previous = _messagePreviewReconcileCts;
                _messagePreviewReconcileCts = fresh;
            }

            CancelAndDispose(previous);
            return fresh.Token;
        }

        private static void CancelAndDispose(CancellationTokenSource source)
        {
            if (source == null)
            {
                return;
            }

            try
            {
                source.Cancel();
                source.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Already retired by the shutdown path.
            }
            catch (AggregateException)
            {
                // Thrown for us by a cancellation callback; the worker it belongs to is going
                // away regardless.
            }
        }

        private async Task RunMessagePreviewReconcileAsync(CancellationToken token, bool full)
        {
            try
            {
                await Task.Delay(MessagePreviewReconcileDebounce, token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                // The source behind this token was retired while we were on our way to the
                // delay. Same meaning as cancellation: a newer reconcile owns the work now.
                return;
            }

            if (_chatService == null)
            {
                return;
            }

            IReadOnlyList<string> jids = null;
            if (!full)
            {
                List<string> queued;
                lock (_messagePreviewReconcileGate)
                {
                    queued = _messagePreviewReconcileQueue;
                    _messagePreviewReconcileQueue = null;
                }

                if (queued == null || queued.Count == 0)
                {
                    return;
                }

                jids = queued;
            }

            TraceListLifecycle(
                "preview-reconcile-enter",
                BuildListSnapshot("full=" + full + "; jids=" + (jids?.Count ?? 0)),
                flush: full);

            try
            {
                await _chatService
                    .ReconcileChatPreviewsFromSqliteAsync(jids, full ? "list-full" : "list-chunk")
                    .ConfigureAwait(false);
                TraceListLifecycle(
                    "preview-reconcile-exit",
                    BuildListSnapshot("full=" + full),
                    flush: full);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[ChatListViewModel] Message preview reconcile failed: " + ex.Message);
                _diagnostics?.RecordException(
                    "chat-list-lifecycle",
                    "preview-reconcile-failed",
                    ex,
                    BuildListSnapshot("full=" + full));
                if (_diagnostics != null)
                {
                    _ = _diagnostics.FlushAsync("chat-list:preview-reconcile-failed");
                }
            }
        }

        private void EnqueueHistoryChatPreviews(IReadOnlyList<HistoryChatPreview> rows)
        {
            if (rows == null || rows.Count == 0)
            {
                return;
            }

            lock (_previewHydrateGate)
            {
                if (_previewHydrateQueue == null)
                {
                    _previewHydrateQueue = new List<HistoryChatPreview>(rows.Count);
                }

                foreach (var row in rows)
                {
                    if (row != null && !string.IsNullOrWhiteSpace(row.Jid))
                    {
                        _previewHydrateQueue.Add(row);
                    }
                }
            }

            EnsurePreviewHydrateLoop();
        }

        private void EnsurePreviewHydrateLoop()
        {
            lock (_previewHydrateGate)
            {
                if (_previewHydrateLoopRunning)
                {
                    return;
                }

                _previewHydrateLoopRunning = true;
            }

            _ = RunPreviewHydrateLoopAsync();
        }

        private async Task RunPreviewHydrateLoopAsync()
        {
            string yesterday = _strings != null
                ? _strings.Get("ChatList_Yesterday", "Yesterday")
                : "Yesterday";

            try
            {
                while (true)
                {
                    List<HistoryChatPreview> slice;
                    lock (_previewHydrateGate)
                    {
                        if (_previewHydrateQueue == null || _previewHydrateQueue.Count == 0)
                        {
                            slice = null;
                        }
                        else
                        {
                            int take = Math.Min(PreviewHydrateBatchSize, _previewHydrateQueue.Count);
                            slice = _previewHydrateQueue.GetRange(0, take);
                            _previewHydrateQueue.RemoveRange(0, take);
                        }
                    }

                    if (slice == null)
                    {
                        await _dispatcher.RunAsync(OnPreviewHydrateQueueDrained).ConfigureAwait(false);
                        return;
                    }

                    await _dispatcher.RunAsync(() => ApplyPreviewHydrateSlice(slice, yesterday))
                        .ConfigureAwait(false);

                    bool more;
                    lock (_previewHydrateGate)
                    {
                        more = _previewHydrateQueue != null && _previewHydrateQueue.Count > 0;
                    }

                    if (more)
                    {
                        await Task.Delay(PreviewHydrateInterval).ConfigureAwait(false);
                    }
                    else
                    {
                        await _dispatcher.RunAsync(OnPreviewHydrateQueueDrained).ConfigureAwait(false);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[ChatListViewModel] Preview hydrate loop failed: " + ex.Message);
                lock (_previewHydrateGate)
                {
                    _previewHydrateLoopRunning = false;
                }
            }
        }

        /// <summary>
        /// After the last SQLite preview slice: if history progress already finalized, finish the
        /// batch banner; otherwise keep showing sync counts until the next quiet finalize.
        /// </summary>
        private void OnPreviewHydrateQueueDrained()
        {
            lock (_previewHydrateGate)
            {
                _previewHydrateLoopRunning = false;
            }

            if (!_batchRendering)
            {
                return;
            }

            _batchProcessed = Math.Max(_batchProcessed, _chatState.Chats.Count);
            RaiseVisibleTarget(_chatState.Chats.Count);

            if (!IsInSafeMode)
            {
                _batchCompleted = true;
                TraceListLifecycle("hydrate-drained-batch-complete", BuildListSnapshot());
                ScheduleFullSqlitePreviewReconcile();
            }
            else
            {
                TraceListLifecycle("hydrate-drained-still-safe", BuildListSnapshot());
            }

            UpdateBatchProgressText();
            EnsureBatchLoop();
        }

        private bool HasPendingPreviewHydrate()
        {
            lock (_previewHydrateGate)
            {
                if (_previewHydrateLoopRunning)
                {
                    return true;
                }

                return _previewHydrateQueue != null && _previewHydrateQueue.Count > 0;
            }
        }

        private void ApplyPreviewHydrateSlice(IReadOnlyList<HistoryChatPreview> rows, string yesterday)
        {
            if (rows == null || rows.Count == 0 || _chatService == null)
            {
                return;
            }

            string selfLabel = _strings != null
                ? _strings.Get("Chat_SelfFallbackName", "You")
                : "You";

            HistoryChatPreviewHydrateResult applied = _chatService.ApplyHistoryChatPreviewHydrate(
                new HistoryChatPreviewHydrateRequest
                {
                    Rows = rows,
                    YesterdayLabel = yesterday,
                    SelfDisplayName = selfLabel
                });

            IReadOnlyList<ChatItem> created = applied?.CreatedChats;
            int createdCount = applied != null ? applied.CreatedCount : 0;
            int updated = applied != null ? applied.UpdatedCount : 0;

            if (created != null)
            {
                for (int i = 0; i < created.Count; i++)
                {
                    ChatItem chat = created[i];
                    if (chat == null)
                    {
                        continue;
                    }

                    chat.PropertyChanged -= ChatItem_PropertyChanged;
                    chat.PropertyChanged += ChatItem_PropertyChanged;
                }
            }

            // Outside the mutation check on purpose: on a reconnect every preview can be older
            // than what is already in memory, so nothing is added or updated - and gating the
            // ceiling on a mutation left the list stuck at one batch for the whole session.
            RaiseVisibleTarget(_chatState.Chats.Count);

            if (createdCount > 0 || updated > 0)
            {
                _batchRendering = true;
                _batchProcessed = Math.Max(_batchProcessed, _chatState.Chats.Count);
                if (_batchTotal < _chatState.Chats.Count)
                {
                    _batchTotal = _chatState.Chats.Count;
                }

                IsLoadingOverlayVisible = VisibleChats.Count == 0;
                UpdateBatchProgressText();
                // Tip-only updates leave VisibleChats full; still re-pin order this turn.
                if (updated > 0 && createdCount == 0 && VisibleChats.Count > 0)
                {
                    ReorderVisibleChatsInPlace();
                }

                EnsureBatchLoop();
            }
        }

        private async void Contacts_DisplayNamesUpdated(object sender, EventArgs e)
        {
            // Group author strips are recomposed by IChatAuthorProjection (app-lifetime, fires even
            // when this list is not on screen). Titles repaint through ChatItem.Name bindings â€”
            // rebuild VisibleChats only when search membership can change (Unigram-style).
            await _dispatcher.RunAsync(() =>
            {
                _searchNameCache.Clear();
                if (!_chatService.RequiresNamesAppliedVisibleRebuild(SearchQuery))
                {
                    return;
                }

                TimeSpan? namesDelay = PreferFrugalUi
                    ? NamesAppliedRefreshDebounceFrugal
                    : (TimeSpan?)null;
                ScheduleRefreshVisibleChats(namesDelay);
            });
        }

        private async void ChatState_ChatsChanged(object sender, NotifyCollectionChangedEventArgs args)
        {
            // Persisted rows are inserted as one startup batch. Queuing a dispatcher callback and
            // a list mutation for every row produced hundreds of pending UI operations on
            // low-memory phones. Subscribe to the items now and let the single history-sync event
            // rebuild the list once.
            if (_history.IsLoadingPersistedChats)
            {
                TrackItems(args);
                return;
            }

            // App-state rows (mute/pin) land before the first history chunk; the completion
            // event releases everything the store holds by then.
            if (IsListHeldForSync)
            {
                TrackItems(args);
                return;
            }

            if (_batchRendering || IsInSafeMode)
            {
                TrackItems(args);
                _batchRendering = true;
                RaiseVisibleTarget(_chatState.Chats.Count);
                EnsureBatchLoop();
                return;
            }

            await _dispatcher.RunAsync(() =>
            {
                if (_chatState.Chats.Count > 0)
                {
                    IsLoadingOverlayVisible = false;
                }

                TrackItems(args);

                bool applied;
                IsRefreshing = true;
                try
                {
                    applied = TryApplyIncrementalChange(args);
                }
                finally
                {
                    IsRefreshing = false;
                }

                if (!applied)
                {
                    ScheduleRefreshVisibleChats();
                    return;
                }

                // After a move or a remove the selected row may be a different instance; rebind.
                RestoreSelection();
            });
        }

        private void NotificationCounter_Changed(object sender, EventArgs e)
        {
            RaiseProperties(nameof(NotificationCounterBadge), nameof(NotificationCounterBadgeText));
        }

        private void TrackItems(NotifyCollectionChangedEventArgs args)
        {
            if (args?.OldItems != null)
            {
                foreach (ChatItem old in args.OldItems)
                {
                    if (old != null) old.PropertyChanged -= ChatItem_PropertyChanged;
                }
            }

            if (args?.NewItems != null)
            {
                foreach (ChatItem added in args.NewItems)
                {
                    if (added == null) continue;
                    added.PropertyChanged -= ChatItem_PropertyChanged;
                    added.PropertyChanged += ChatItem_PropertyChanged;
                }
            }
        }

        private void ChatItem_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Row bindings repaint themselves. Rebuilding or reordering the whole list for every
            // preview and timestamp change is what created the dispatcher backlog during history
            // sync, so only the two things that can change the *order* or the *membership* of the
            // list get to trigger a refresh.
            if (_chatService.RequiresVisibleReorder(e.PropertyName))
            {
                // App-state delivers pins while history sync is still filling the list, and the
                // debounced refresh is suppressed for the whole of it. Mark the order stale so the
                // batch loop re-sorts in place; otherwise the pin only reaches the top of the list
                // after the sync ends.
                _visibleOrderDirty = true;
                // While the first sync holds the list, the release builds it sorted from scratch;
                // a dispatcher post per preview hydrate would only queue no-ops on the UI thread.
                if (!IsListHeldForSync)
            {
                _ = _dispatcher.RunAsync(() => ScheduleRefreshVisibleChats());
                }

                return;
            }

            if (IsListHeldForSync || string.IsNullOrWhiteSpace(SearchQuery))
            {
                return;
            }

            if (e.PropertyName == nameof(ChatItem.Name) ||
                e.PropertyName == nameof(ChatItem.LastMessage) ||
                e.PropertyName == nameof(ChatItem.LastMessageAuthor) ||
                e.PropertyName == nameof(ChatItem.LastMessageSenderName) ||
                e.PropertyName == nameof(ChatItem.MentionLookup) ||
                e.PropertyName == nameof(ChatItem.Timestamp))
            {
                _ = _dispatcher.RunAsync(() => ScheduleRefreshVisibleChats());
            }
        }

    }
}
