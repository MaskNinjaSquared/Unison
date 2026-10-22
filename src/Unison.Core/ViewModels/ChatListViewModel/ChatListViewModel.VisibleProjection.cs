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
        // Building the visible list
        // ---------------------------------------------------------------------

        private void ScheduleRefreshVisibleChats(
            TimeSpan? delayOverride = null,
            bool allowDuringEnrichment = false)
        {
            if (_batchRendering || IsInSafeMode || IsListHeldForSync)
            {
                return;
            }

            // Frugal Mobile: hold reshuffles while background names/avatars/groups enrich so
            // Move/Insert thrash does not stack on top of PropertyChanged storms. Search and
            // explicit list-filter changes still refresh so the UI stays responsive.
            if (!allowDuringEnrichment &&
                PreferFrugalUi &&
                string.IsNullOrWhiteSpace(SearchQuery) &&
                _chatService.MatchesListEnrichmentPhase(_lastSyncPhaseToken))
            {
                _refreshDeferredForEnrichment = true;
                _visibleOrderDirty = true;
                return;
            }

            _refreshCts?.Cancel();
            _refreshContinueCts?.Cancel();
            // A fresh schedule invalidates any in-flight continue; drop the cached order so the
            // next turn rebuilds projection from the store.
            _pendingVisibleRefreshSource = null;
            _refreshCts = new CancellationTokenSource();
            var token = _refreshCts.Token;
            TimeSpan delay = delayOverride ?? (PreferFrugalUi
                ? VisibleRefreshDebounceFrugal
                : VisibleRefreshDebounceGenerous);

            Task.Delay(delay, token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                _ = _dispatcher.RunAsync(() => RefreshVisibleChats());
            });
        }

        /// <summary>
        /// Rebuilds the visible order in place: search, collapse PN/LID duplicates, project
        /// community hubs, then apply the active list filter, sort pinned first and by recency.
        /// Filter runs after projection so Groups/Communities cannot be undone by hub re-inject.
        /// Budgeted continues pass <paramref name="reusePendingSource"/> so each slice only
        /// Moves instead of re-projecting the whole catalogue on the UI thread.
        /// </summary>
        public void RefreshVisibleChats(bool reusePendingSource = false)
        {
            if (_batchRendering || IsInSafeMode || IsListHeldForSync)
            {
                return;
            }

            int visibleBefore = VisibleChats.Count;
            TraceListLifecycle(
                "refresh-visible-enter",
                BuildListSnapshot("before=" + visibleBefore),
                flush: true);

            _visibleOrderDirty = false;

            List<ChatItem> source = null;
            bool reusedSource = reusePendingSource && _pendingVisibleRefreshSource != null;
            if (reusedSource)
            {
                source = _pendingVisibleRefreshSource;
            }
            else
            {
                // A full rebuild supersedes any in-flight continue slice.
                _refreshContinueCts?.Cancel();
                source = BuildVisibleRefreshSource();
                _pendingVisibleRefreshSource = source;
            }

            int moveBudget = PreferFrugalUi
                ? VisibleRefreshMoveBudgetFrugal
                : VisibleRefreshMoveBudgetGenerous;

            ChatListVisibleReconcileResult reconcile;
            IsRefreshing = true;
            try
            {
                reconcile = _chatService.ReconcileVisibleChats(VisibleChats, source, moveBudget);
            }
            finally
            {
                IsRefreshing = false;
            }

            RestoreSelection();

            TraceListLifecycle(
                "refresh-visible-exit",
                BuildListSnapshot(
                    "before=" + visibleBefore +
                    "; source=" + source.Count +
                    "; mutations=" + reconcile.Mutations +
                    "; incomplete=" + reconcile.Incomplete +
                    "; budget=" + moveBudget +
                    "; reused=" + reusedSource),
                flush: true);

            if (reconcile.Incomplete)
            {
                ScheduleRefreshVisibleChatsContinue();
            }
            else
            {
                _pendingVisibleRefreshSource = null;
            }
        }

        /// <summary>
        /// Projects the catalogue into the order VisibleChats should show. Expensive on frugal
        /// ARM â€” callers must not re-run this on every budgeted continue slice.
        /// </summary>
        private List<ChatItem> BuildVisibleRefreshSource()
        {
            ApplyLocalChatFieldsToScopedChats();

            string query = SearchQuery?.Trim() ?? string.Empty;
            var source = _chatState.Chats.Where(MatchesScope).ToList();

            if (!string.IsNullOrEmpty(query))
            {
                source = source.Where(c => MatchesQuery(c, query)).ToList();
            }

            if (_chatService == null)
            {
                return source;
            }

            source = _chatService.DeduplicateChatsByCanonicalJid(source, GetCanonical);
            source = _chatService.ProjectCommunityList(
                source,
                ResolveCatalogByJid,
                GetOrCreateSyntheticCommunity,
                GetCanonical);
            EnrichCommunityHubRows(source);

            if (_activeFilter != ChatListFilter.All)
            {
                source = source.Where(MatchesActiveFilter).ToList();
            }

            source = _chatService.SortChatsForDisplay(source);

            if (_activeCommunity != null)
            {
                ChatItem stillOpen = FindProjectedCommunity(_activeCommunity.JID, source);
                if (stillOpen == null)
                {
                    ActiveCommunity = null;
                }
                else if (!ReferenceEquals(stillOpen, _activeCommunity))
                {
                    ActiveCommunity = stillOpen;
                }
                else
                {
                    OnPropertyChanged(nameof(ActiveCommunitySubtitle));
                    OnPropertyChanged(nameof(ActiveCommunityTitle));
                }
            }

            return source;
        }

        /// <summary>
        /// Continues a budgeted refresh on the next dispatcher turn so one sync chunk cannot
        /// hold the UI thread for hundreds of Moves.
        /// </summary>
        private void ScheduleRefreshVisibleChatsContinue()
        {
            _refreshContinueCts?.Cancel();
            _refreshContinueCts = new CancellationTokenSource();
            var token = _refreshContinueCts.Token;
            TimeSpan delay = PreferFrugalUi
                ? TimeSpan.FromMilliseconds(40)
                : TimeSpan.FromMilliseconds(16);

            Task.Delay(delay, token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                _ = _dispatcher.RunAsync(() => RefreshVisibleChats(reusePendingSource: true));
            });
        }

        private bool PreferFrugalUi => _history.PreferFrugalSyncBudget;

        /// <summary>
        /// Accepts the flyout's integer id, maps it onto <see cref="ChatListFilter"/>, and
        /// refreshes. Unknown ids are ignored so a mistyped parameter cannot blank the list.
        /// </summary>
        private void ApplyChatListFilter(int filterId)
        {
            if (!Enum.IsDefined(typeof(ChatListFilter), filterId))
            {
                return;
            }

            var next = (ChatListFilter)filterId;
            if (next == ActiveFilter)
            {
                return;
            }

            // Hub overlay covers the whole list in Legacy; close so the filtered rows are visible.
            if (_activeCommunity != null)
            {
                CloseCommunityHub();
            }

            ActiveFilter = next;
            _visibleOrderDirty = true;

            // Batch / first-sync hold: ActiveFilter sticks; the next full refresh applies it.
            if (_batchRendering || IsInSafeMode || IsListHeldForSync)
            {
                return;
            }

            // User-driven: paint immediately â€” do not sit behind enrichment debounce/hold.
            _refreshCts?.Cancel();
            _refreshContinueCts?.Cancel();
            _pendingVisibleRefreshSource = null;
            _refreshDeferredForEnrichment = false;
            _ = _dispatcher.RunAsync(() => RefreshVisibleChats());
        }

        /// <summary>
        /// Whether a row belongs under the current list filter. Search is applied separately so
        /// the two constraints compose with AND rather than competing for one predicate.
        /// </summary>
        private bool MatchesActiveFilter(ChatItem chat)
        {
            if (chat == null)
            {
                return false;
            }

            switch (_activeFilter)
            {
                case ChatListFilter.All:
                    return true;

                case ChatListFilter.Unread:
                    return chat.HasUnread;

                case ChatListFilter.Favorites:
                    return chat.IsFavorite;

                case ChatListFilter.Contacts:
                    return !chat.IsGroup && IsAddressBookContact(chat);

                case ChatListFilter.NonContacts:
                    return chat.IsDirect && !IsAddressBookContact(chat);

                case ChatListFilter.Groups:
                    return chat.IsGroup && !chat.IsCommunity && !chat.IsCommunityParent;

                case ChatListFilter.Drafts:
                    return chat.HasDraft;

                case ChatListFilter.Communities:
                    return chat.IsCommunity || chat.IsCommunityParent;

                default:
                    return true;
            }
        }

        private bool MatchesScope(ChatItem chat)
        {
            if (chat == null)
            {
                return false;
            }

            return _scope == ChatListScope.Archived
                ? chat.Status == ChatStatus.Archived
                : chat.Status == ChatStatus.Active;
        }

        /// <summary>
        /// True when this JID (or its canonical form) is in the device address-book overlay â€”
        /// the same map WhatsApp uses to prefer phone-book names over push names.
        /// </summary>
        private bool IsAddressBookContact(ChatItem chat)
        {
            if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
            {
                return false;
            }

            var names = _contactService.PhoneContactNamesByJid;
            if (names == null || names.Count == 0)
            {
                return false;
            }

            if (names.ContainsKey(chat.JID))
            {
                return true;
            }

            string canonical = _jids.GetCanonicalJid(chat.JID);
            return !string.IsNullOrWhiteSpace(canonical) && names.ContainsKey(canonical);
        }

        private bool MatchesQuery(ChatItem chat, string query)
        {
            return _chatService != null &&
                   _chatService.MatchesChatListQuery(
                       chat,
                       query,
                       () => CachedSearchName(chat?.JID, "search"),
                       jid => CachedSearchName(jid, "mention"));
        }

        /// <summary>
        /// One resolved label per JID and call-site context. Cleared when contact names are
        /// published, so a later address-book or push name is not stuck behind the first answer.
        /// </summary>
        private string CachedSearchName(string jid, string context)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return string.Empty;
            }

            string key = (context ?? string.Empty) + "\n" + jid;
            if (_searchNameCache.TryGetValue(key, out string cached))
            {
                return cached;
            }

            string resolved = _contactService.ResolveDisplayName(jid, context) ?? string.Empty;
            _searchNameCache[key] = resolved;
            return resolved;
        }


        /// <summary>
        /// Applies a single source change straight to the visible list instead of rebuilding it.
        /// Only valid without a search or list filter, where the visible list is the source list;
        /// with either filter, whether a row belongs on screen is a question only a rebuild can
        /// answer. Returns false when the caller should rebuild.
        /// </summary>
        private bool TryApplyIncrementalChange(NotifyCollectionChangedEventArgs args)
        {
            if (args == null ||
                !string.IsNullOrWhiteSpace(SearchQuery) ||
                _activeFilter != ChatListFilter.All)
            {
                return false;
            }

            switch (args.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    return TryInsert(args.NewItems);

                case NotifyCollectionChangedAction.Remove:
                    if (args.OldItems == null) return false;
                    RemoveAll(args.OldItems);
                    return true;

                case NotifyCollectionChangedAction.Move:
                    if (args.NewItems == null || args.NewItems.Count != 1) return false;
                    return MoveToMatchSource(args.NewItems[0] as ChatItem);

                case NotifyCollectionChangedAction.Replace:
                    if (args.OldItems == null || args.NewItems == null) return false;
                    RemoveAll(args.OldItems);
                    return TryInsert(args.NewItems);

                default:
                    return false;
            }
        }

        private bool TryInsert(System.Collections.IList items)
        {
            if (items == null)
            {
                return false;
            }

            foreach (ChatItem item in items)
            {
                if (item == null)
                {
                    continue;
                }

                // Community projection must rebuild the visible list as a whole.
                if (item.IsCommunityParent ||
                    item.IsCommunity ||
                    !string.IsNullOrWhiteSpace(item.LinkedParentJid))
                {
                    return false;
                }

                if (!MatchesScope(item) || VisibleChats.Contains(item))
                {
                    continue;
                }

                int targetIndex = _chatState.Chats.Where(MatchesScope).ToList().IndexOf(item);
                if (targetIndex < 0)
                {
                    return false;
                }

                VisibleChats.Insert(Math.Min(targetIndex, VisibleChats.Count), item);
            }

            return true;
        }

        private ChatItem ResolveCatalogByJid(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return null;
            }

            string key = GetCanonical(jid);
            foreach (ChatItem chat in _chatState.Chats)
            {
                if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
                {
                    continue;
                }

                if (string.Equals(GetCanonical(chat.JID), key, StringComparison.OrdinalIgnoreCase))
                {
                    return chat;
                }
            }

            return null;
        }

        private ChatItem GetOrCreateSyntheticCommunity(string parentJid)
        {
            string key = GetCanonical(parentJid);
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            ChatItem existing;
            if (_syntheticCommunities.TryGetValue(key, out existing) && existing != null)
            {
                return existing;
            }

            var created = new ChatItem
            {
                Id = key,
                JID = key,
                Status = ChatStatus.Active,
                IsCommunityParent = true
            };
            _syntheticCommunities[key] = created;
            return created;
        }

        private static ChatItem FindProjectedCommunity(string jid, List<ChatItem> source)
        {
            if (string.IsNullOrWhiteSpace(jid) || source == null)
            {
                return null;
            }

            for (int i = 0; i < source.Count; i++)
            {
                ChatItem chat = source[i];
                if (chat != null &&
                    (chat.IsCommunity || chat.IsCommunityParent) &&
                    string.Equals(chat.JID, jid, StringComparison.OrdinalIgnoreCase))
                {
                    return chat;
                }
            }

            return null;
        }

        private void RemoveAll(System.Collections.IList items)
        {
            foreach (ChatItem item in items)
            {
                if (item == null) continue;
                int index = VisibleChats.IndexOf(item);
                if (index >= 0)
                {
                    VisibleChats.RemoveAt(index);
                }
            }
        }

        private bool MoveToMatchSource(ChatItem chat)
        {
            if (chat == null)
            {
                return false;
            }

            if (!MatchesScope(chat))
            {
                int hiddenIndex = VisibleChats.IndexOf(chat);
                if (hiddenIndex >= 0)
                {
                    VisibleChats.RemoveAt(hiddenIndex);
                }
                return true;
            }

            // Pin-aware index â€” mirroring raw Chats order after Move(0) put unpinned tips on top.
            if (_chatService == null)
            {
                return false;
            }

            var ordered = _chatService.SortChatsForDisplay(
                _chatState.Chats.Where(c => c != null && MatchesScope(c)));
            int targetIndex = -1;
            for (int i = 0; i < ordered.Count; i++)
            {
                if (ReferenceEquals(ordered[i], chat))
                {
                    targetIndex = i;
                    break;
                }
            }

            int visibleIndex = VisibleChats.IndexOf(chat);
            if (targetIndex < 0 || visibleIndex < 0)
            {
                return false;
            }

            targetIndex = Math.Min(targetIndex, VisibleChats.Count - 1);
            if (targetIndex < 0)
            {
                return false;
            }

            if (visibleIndex != targetIndex)
            {
                VisibleChats.Move(visibleIndex, targetIndex);
            }

            return true;
        }

        /// <summary>
        /// Points the selection back at the open conversation after the list moved underneath it.
        /// When the row is momentarily gone - a PN/LID merge removes and re-adds it - the JID is
        /// kept so a later change can find it again.
        /// </summary>
        private void RestoreSelection()
        {
            if (string.IsNullOrWhiteSpace(_lastSelectedChatJid))
            {
                return;
            }

            var restored = FindVisibleByJid(_lastSelectedChatJid);
            if (restored == null)
            {
                return;
            }

            _lastSelectedChatJid = restored.JID;
            SelectedChat = restored;
            SelectionRestored?.Invoke(this, restored);
        }

        private ChatItem FindVisibleByJid(string jid)
        {
            string canonical = GetCanonical(jid);
            return VisibleChats.FirstOrDefault(c =>
                c != null &&
                (string.Equals(c.JID, jid, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(GetCanonical(c.JID), canonical, StringComparison.OrdinalIgnoreCase)));
        }

        private string GetCanonical(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return jid;
            }

            string canonical = _jids.GetCanonicalJid(jid);
            return string.IsNullOrWhiteSpace(canonical) ? jid : canonical;
        }

        private bool IsInSafeMode => _history.IsInitialSyncSafeMode;

        /// <summary>
        /// The first history sync persists before any row reaches the screen, so the UI thread is
        /// not fighting that bulk SQLite write. Toast deep-link also delays first materialize until
        /// the conversation has loaded.
        /// </summary>
        private bool IsListHeldForSync => _chatService.ShouldHoldChatListRows(
            _history.IsAwaitingFirstHistorySync,
            _deepLinkHold != null && _deepLinkHold.IsHeld,
            _listEverMaterialized);

        private bool IsDeepLinkHeld => _deepLinkHold != null && _deepLinkHold.IsHeld;

        private bool IsDeepLinkHeldPreferLoading =>
            _chatService.ShouldPreferDeepLinkLoadingOverlay(IsDeepLinkHeld, _listEverMaterialized);

        private void DeepLinkHold_Changed(object sender, EventArgs e)
        {
            if (!_attached)
            {
                return;
            }

            RaiseChatListMaterialized();
            if (!IsListHeldForSync && !_batchRendering)
            {
                RefreshVisibleChats();
                NotifyListPaintReleased("deep-link-hold-released");
            }

            // Hold: stash any visible sync line and keep ChatList_Loading until conversation ready.
            // Release: re-present the deferred sync banner (catch-up, progress, etc.).
            if (IsDeepLinkHeld)
            {
                if (IsSyncStatusVisible && !string.IsNullOrEmpty(SyncStatusText))
                {
                    _pendingDeepLinkSyncMessage = SyncStatusText;
                    _pendingDeepLinkSyncVisible = true;
                }

                if (IsDeepLinkHeldPreferLoading)
                {
                    ShowDeepLinkLoadingOverlay();
                }
            }
            else
            {
                _ = FlushPendingDeepLinkSyncStatusAsync();
            }
        }

        private void ShowDeepLinkLoadingOverlay()
        {
            LoadingOverlayText = _strings == null
                ? string.Empty
                : _strings.Get("ChatList_Loading.Text", "Preparing conversations...");
            IsLoadingOverlayVisible = true;
            IsSyncStatusVisible = false;
            SyncStatusText = string.Empty;
        }

        private async Task FlushPendingDeepLinkSyncStatusAsync()
        {
            string pending = _pendingDeepLinkSyncMessage;
            bool visible = _pendingDeepLinkSyncVisible;
            _pendingDeepLinkSyncMessage = null;
            _pendingDeepLinkSyncVisible = false;

            if (_batchRendering)
            {
                UpdateBatchProgressText();
                return;
            }

            if (visible && !string.IsNullOrEmpty(pending))
            {
                await PresentSyncStatusAsync(pending, visible: true, source: "deep-link-hold-released")
                    .ConfigureAwait(false);
            }
        }

        private void RaiseChatListMaterialized()
        {
            bool materialized = IsChatListMaterialized;
            if (materialized)
            {
                _listEverMaterialized = true;
            }
            else if (_history.IsAwaitingFirstHistorySync)
            {
                // Wipe / first-sync hold again: allow a later toast to delay the next first paint.
                _listEverMaterialized = false;
            }

            RaiseProperties(nameof(IsChatListMaterialized));
            TraceListLifecycle(
                "raise-materialized",
                BuildListSnapshot("notify=" + materialized),
                flush: materialized);
        }

    }
}
