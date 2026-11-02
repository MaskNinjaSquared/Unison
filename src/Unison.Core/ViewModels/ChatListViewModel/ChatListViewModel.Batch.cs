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
        // Batch rendering
        // ---------------------------------------------------------------------

        /// <summary>Banner and counts only â€” no VisibleChats mutations yet.</summary>
        private void ShowSyncProgressOnly(int processed, int total)
        {
            _batchProcessed = Math.Max(_batchProcessed, Math.Max(0, processed));
            _batchTotal = Math.Max(_batchTotal, Math.Max(0, total));
            IsLoadingOverlayVisible = true;
            if (IsDeepLinkHeldPreferLoading)
            {
                ShowDeepLinkLoadingOverlay();
                return;
            }

            UpdateBatchProgressText();
        }

        private void BeginBatchRendering(int processed, int total, int visibleTarget)
        {
            bool fresh = !_batchRendering || _batchCompleted;
            _batchRendering = true;
            _batchCompleted = false;
            if (fresh)
            {
                _batchProcessed = Math.Max(0, processed);
                _batchTotal = Math.Max(0, total);
            }
            else
            {
            _batchProcessed = Math.Max(_batchProcessed, processed);
            _batchTotal = Math.Max(_batchTotal, total);
            }

            // Never clamped to the store count: this event is raised before the chunk's previews
            // are persisted, so on the first chunk the store is still empty and clamping pinned
            // the target at zero - which left the list frozen at one batch until the whole sync
            // finalized. The release pace is bounded by the per-turn budget, not by this number.
            RaiseVisibleTarget(Math.Max(visibleTarget, processed));

            IsLoadingOverlayVisible = VisibleChats.Count == 0;
            UpdateBatchProgressText();
            TraceListLifecycle(
                "begin-batch",
                BuildListSnapshot("fresh=" + fresh + "; argTarget=" + visibleTarget),
                flush: true);
            EnsureBatchLoop();
        }

        /// <summary>
        /// Grows the ceiling on how many rows may be on screen. Monotonic: a later event that
        /// knows less than an earlier one must not shrink the list back.
        /// </summary>
        private void RaiseVisibleTarget(int candidate)
        {
            int target = Math.Max(_chatState.Chats.Count, Math.Max(BatchSize, candidate));
            if (target > _batchVisibleTarget)
            {
                _batchVisibleTarget = target;
            }
        }

        private void EnsureBatchLoop()
        {
            if (_batchLoopRunning)
            {
                return;
            }

            _batchLoopRunning = true;
            TraceListLifecycle("batch-loop-start", BuildListSnapshot());
            _ = RunBatchLoopAsync();
        }

        private async Task RunBatchLoopAsync()
        {
            try
            {
                // Held: stop polling; the completion event restarts the loop.
                while (_batchRendering && !IsListHeldForSync)
                {
                    await Task.Delay(BatchInterval).ConfigureAwait(false);
                    await _dispatcher.RunAsync(ReleaseNextBatch);
                }

                TraceListLifecycle(
                    "batch-loop-exit",
                    BuildListSnapshot("stillBatch=" + _batchRendering + "; held=" + IsListHeldForSync));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatListViewModel] Batch render loop failed: " + ex.Message);
                _diagnostics?.RecordException("chat-list-lifecycle", "batch-loop-failed", ex, BuildListSnapshot());
                if (_diagnostics != null)
                {
                    _ = _diagnostics.FlushAsync("chat-list:batch-loop-failed");
                }
            }
            finally
            {
                _batchLoopRunning = false;
            }
        }

        /// <summary>
        /// Moves at most one batch of conversations from the store onto the screen, newest first,
        /// and stops the loop once everything the source holds is visible.
        /// </summary>
        private void ReleaseNextBatch()
        {
            if (!_batchRendering || IsListHeldForSync)
            {
                return;
            }

            _postSyncReleaseTicks++;
            int tick = _postSyncReleaseTicks;
            bool logTick = tick <= 3 || (tick % 5) == 0 || _batchCompleted;
            if (logTick)
            {
                TraceListLifecycle(
                    "release-tick-enter",
                    BuildListSnapshot("tick=" + tick),
                    flush: tick <= 2 || _batchCompleted);
            }

            // Pin/mute live in ChatStore, not history_chat_preview. Apply before sorting so
            // pinned rows are not released mid-list and only "jump up" after opening a chat.
            ApplyLocalChatFieldsToScopedChats();

            int scopedCount = _chatState.Chats.Count(MatchesScope);
            if (!_batchCompleted && VisibleChats.Count >= scopedCount)
            {
                // Tips keep updating while the list is already full; still keep pin order.
                ReorderVisibleChatsInPlace();
                // Hydrate can stall with pending=True forever; once every scoped row is on
                // screen after the hold, open the enrich gate and finish the batch next tick.
                if (!IsListHeldForSync && !IsInSafeMode)
                {
                    if (!HasPendingPreviewHydrate())
                    {
                        _batchCompleted = true;
                        TraceListLifecycle("batch-complete-visible-caught-up", BuildListSnapshot());
                    }
                    else
                    {
                        NotifyListPaintReleased("visible-caught-up-hydrate-pending");
                    }
                }

                return;
            }

            var scoped = _chatState.Chats.Where(c =>
                c != null &&
                !string.IsNullOrWhiteSpace(c.JID) &&
                MatchesScope(c));
            var source = _chatService != null
                ? _chatService.SortChatsForDisplay(scoped)
                : scoped.ToList();

            int desiredCount = _batchCompleted
                ? source.Count
                : Math.Min(source.Count, Math.Max(_batchVisibleTarget, BatchSize));

            var visibleCanonical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var visible in VisibleChats)
            {
                if (visible == null) continue;
                visibleCanonical.Add(GetCanonical(visible.JID));
            }

            var budget = Stopwatch.StartNew();
            int added = 0;
            foreach (var chat in source)
            {
                if (VisibleChats.Count >= desiredCount)
                {
                    break;
                }

                // A fixed count either starves a fast machine or stalls a slow one. Release one
                // batch unconditionally, then keep going while this turn still has time, so the
                // list fills as fast as the device allows without eating an input frame.
                if (added >= BatchSize && budget.ElapsedMilliseconds >= BatchBudgetMs)
                {
                    break;
                }

                if (!visibleCanonical.Add(GetCanonical(chat.JID)))
                {
                    continue;
                }

                VisibleChats.Add(chat);
                added++;
            }

            if (VisibleChats.Count > 0)
            {
                IsLoadingOverlayVisible = false;
            }

            // A pin that landed on a row already on screen cannot move it by itself: the release
            // loop only appends. Without this the conversation sat wherever its tip put it until
            // the sync finished.
            if (_visibleOrderDirty)
            {
                ReorderVisibleChatsInPlace();
            }

            if (logTick)
            {
                TraceListLifecycle(
                    "release-tick-exit",
                    BuildListSnapshot(
                        "tick=" + tick +
                        "; added=" + added +
                        "; desired=" + desiredCount +
                        "; scoped=" + scopedCount +
                        "; ms=" + budget.ElapsedMilliseconds),
                    flush: tick <= 2 || _batchCompleted);
            }

            if (!_batchCompleted)
            {
                if (added > 0)
                {
                    UpdateBatchProgressText();
                }

                return;
            }

            int uniqueSourceCount = source
                .Select(c => GetCanonical(c.JID))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            if (VisibleChats.Count < uniqueSourceCount)
            {
                TraceListLifecycle(
                    "release-awaiting-full",
                    BuildListSnapshot(
                        "unique=" + uniqueSourceCount + "; visible=" + VisibleChats.Count),
                    flush: true);
                return;
            }

            TraceListLifecycle("batch-complete-before-refresh", BuildListSnapshot(), flush: true);
            _batchRendering = false;
            _batchCompleted = false;
            RefreshVisibleChats();
            TraceListLifecycle("batch-complete-after-refresh", BuildListSnapshot(), flush: true);

            // Safe mode can outlive the batch loop, and the full refresh above is gated on it. The
            // in-place reorder is not, so a pin that arrived late still reaches the top.
            if (_visibleOrderDirty)
            {
                ReorderVisibleChatsInPlace();
            }
            _ = PresentSyncStatusAsync(null, visible: false, source: "IHistoryService:batch-complete");
            IsLoadingOverlayVisible = false;
            TraceListLifecycle("batch-complete-done", BuildListSnapshot(), flush: true);
            NotifyListPaintReleased("batch-complete-done");
        }

        private void NotifyListPaintReleased(string reason)
        {
            if (_listPaintGate == null)
            {
                return;
            }

            TraceListLifecycle("list-paint-gate-release", BuildListSnapshot("reason=" + reason), flush: true);
            _listPaintGate.MarkReleased(reason);
        }

        /// <summary>
        /// Loads ChatStore pin/mute onto every scoped row before SortForDisplay / VisibleChats.
        /// </summary>
        private void ApplyLocalChatFieldsToScopedChats()
        {
            if (_chatStore == null)
            {
                return;
            }

            for (int i = 0; i < _chatState.Chats.Count; i++)
            {
                ChatItem chat = _chatState.Chats[i];
                if (chat == null || string.IsNullOrWhiteSpace(chat.JID) || !MatchesScope(chat))
                {
                    continue;
                }

                try
                {
                    _chatStore.ApplyTo(chat);
                    _chatService?.ApplyLocalStateAliasFallback(chat);

                    // History / app-state can pin the row in memory before ChatStore has a known
                    // pin under this exact JID. Mirror it into the cache so the next ApplyTo
                    // (another batch tick) cannot treat a mute-only stub as "not pinned".
                    if (chat.IsChatPinned)
                    {
                        _chatService?.RememberPinnedUnderAliases(chat.JID, true);
                    }

                    if (chat.MutedUntil != null)
                    {
                        _chatStore.RememberMutedUntil(chat.JID, chat.MutedUntil);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[ChatListViewModel] ApplyTo (batch) failed: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Re-sorts <see cref="VisibleChats"/> during batch/sync without clearing the list â€”
        /// keeps pinned conversations at the top while tips keep arriving.
        /// </summary>
        private void ReorderVisibleChatsInPlace()
        {
            // VisibleChats is already community-projected. Pin/recency reshuffle must not call
            // BuildVisibleRefreshSource just because a hub exists in the catalogue — that was the
            // Mobile Low ~10s buildMs hitch after enrichment (hubs present → full rebuild).
            _visibleOrderDirty = false;

            if (VisibleChats.Count < 2)
            {
                return;
            }

            ApplyLocalChatFieldsToScopedChats();

            var desired = _chatService != null
                ? _chatService.SortChatsForDisplay(
                    VisibleChats.Where(c => c != null && !string.IsNullOrWhiteSpace(c.JID)))
                : VisibleChats.Where(c => c != null && !string.IsNullOrWhiteSpace(c.JID)).ToList();

            IsRefreshing = true;
            try
            {
                for (int i = 0; i < desired.Count; i++)
                {
                    ChatItem item = desired[i];
                    if (i < VisibleChats.Count && ReferenceEquals(VisibleChats[i], item))
                    {
                        continue;
                    }

                    int existingIndex = VisibleChats.IndexOf(item);
                    if (existingIndex >= 0 && existingIndex != i)
                    {
                        VisibleChats.Move(existingIndex, i);
                    }
                }
            }
            finally
            {
                IsRefreshing = false;
            }
        }

        private void UpdateBatchProgressText()
        {
            if (IsDeepLinkHeldPreferLoading)
            {
                ShowDeepLinkLoadingOverlay();
                return;
            }

            if (_batchCompleted)
            {
                _ = PresentSyncStatusAsync(_strings.Get("ChatList_SyncDone"), visible: true);
                LoadingOverlayText = _strings.Get("ChatList_Organizing");
                return;
            }

            // Counts rows, not conversations. The sync figure climbed into the hundreds while the
            // list showed twenty, because it measured what the chunk carried rather than what
            // reached the screen - which is the part the user is actually waiting on.
            int shown = VisibleChats.Count;
            int available = Math.Max(_chatState.Chats.Count, shown);

            string text = available > 0
                ? FormatString("ChatList_SyncProgress", shown, available)
                : FormatString("ChatList_SyncProgressCount", _batchProcessed);

            _ = PresentSyncStatusAsync(text, visible: true);
            LoadingOverlayText = text;
        }

        /// <summary>
        /// A resource string with its placeholders filled. Falls back to the raw string when it
        /// does not match the arguments, so a bad translation shows something rather than throwing
        /// in the middle of a sync.
        /// </summary>
        private string FormatString(string key, params object[] args)
        {
            string format = _strings.Get(key);
            try
            {
                return string.Format(format, args);
            }
            catch (FormatException)
            {
                return format;
            }
        }
    }
}
