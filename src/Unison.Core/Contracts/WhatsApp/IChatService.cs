// =============================================================================
// IChatService
//
// What the user does to a conversation as a whole, as opposed to what they do
// inside one.
//
// Pinning and marking read are account-wide facts, not local preferences: the
// phone and every other linked device are meant to agree, so both of these go
// out on the wire and come back as app state. The local copy is kept anyway,
// because the list has to sort and draw before any of that round trips - and
// has to keep working with no connection at all.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts.WhatsApp
{
    public interface IChatService
    {
        /// <summary>
        /// Pins the conversation to the top of the list, for this account everywhere. Distinct
        /// from <see cref="ChatItem.IsWidgetPinned"/>, which is a tile on this device's Start
        /// screen and means nothing to WhatsApp.
        /// </summary>
        Task SetPinnedAsync(ChatItem chat, bool pinned);

        /// <summary>
        /// Adds or removes the conversation from WhatsApp Favourites (account-wide
        /// <c>favoritesAction</c>). Local membership is updated first; the wire patch replaces
        /// the full favourites list.
        /// </summary>
        Task SetFavoriteAsync(ChatItem chat, bool favorite);

        /// <summary>
        /// Marks everything in the conversation as read: the senders are told, and the unread
        /// badge is cleared here and on the phone. Does nothing when there was nothing unread,
        /// so it is safe to call every time a chat is opened.
        /// </summary>
        Task MarkReadAsync(ChatItem chat);

        /// <summary>
        /// After the open-session unread chip is placed, mark inbound history rows as read
        /// (<c>IsRead = 1</c>). Null / missing column is ignored — no schema migration.
        /// </summary>
        Task CommitInboundHistoryReadAsync(ChatItem chat);

        /// <summary>
        /// Deletes the conversation for this account everywhere: it leaves the list here, on the
        /// phone and on every other linked device. The messages are removed locally too - unlike a
        /// pin there is nothing to revert to, so this is not undoable and callers are expected to
        /// have asked first.
        /// </summary>
        Task DeleteChatAsync(ChatItem chat);

        /// <summary>
        /// Account-side (or local) chat deletion: drop list rows and the in-memory timeline,
        /// forget missing-message / on-demand history state, then durable storage via the host.
        /// </summary>
        Task ApplyAppStateDeleteChatAsync(string jid);

        /// <summary>
        /// Which conversation is on screen. The notification path reads it to stay quiet about the
        /// chat the user is already looking at; null means none.
        /// </summary>
        void SetActiveChatJid(string jid);

        /// <summary>User action: makes sure a chat row exists for <paramref name="jid"/> (new-chat flow).</summary>
        void StartNewChat(string jid);

        /// <summary>
        /// Zeroes the unread count locally, on every row that is the same conversation. PN and LID
        /// aliases can briefly produce more than one row, and a leftover alias is enough to put the
        /// badge back on a chat the user just read. This is the local half only - the account is
        /// told by <see cref="MarkReadAsync"/>.
        /// </summary>
        Task ClearUnreadForChatAsync(string jid);

        /// <summary>Unread across every conversation, for the tile and the taskbar badge.</summary>
        int GetTotalUnreadCount();

        /// <summary>
        /// Writes just these list rows to the preview store. For a caller that already changed the
        /// strip on screen and only needs the mirror to agree; it does not rewrite names or aliases.
        /// </summary>
        void PersistChatListRows(IList<ChatItem> chats);

        /// <summary>
        /// Realigns the list strip with the newest row in SQLite where the list fell behind - a
        /// send from another device, or a history chunk that stored messages without touching the
        /// preview. Null checks every open row.
        /// </summary>
        Task ReconcileChatPreviewsFromSqliteAsync(
            IReadOnlyList<string> chatJids = null,
            string reason = null);

        /// <summary>
        /// Live inbound: create/find the list row, write the strip tip, bump unread when
        /// <see cref="LiveIncomingChatListApplyRequest.CountsAsUnread"/>, reposition for display.
        /// Must run list mutations on the UI thread. Returns the row and total unread for toast.
        /// </summary>
        Task<LiveIncomingChatListApplyResult> ApplyLiveIncomingChatListAsync(
            LiveIncomingChatListApplyRequest request);

        /// <summary>
        /// Folds one replayed message into its chat's tip. Unread is decided here, once; the
        /// first tip arms a short timer that flushes the tips onto the list.
        /// </summary>
        void RecordOfflineReplayTip(OfflineReplayTipRequest request);

        /// <summary>
        /// After an incoming envelope is accepted on the timeline: refresh the list tip live,
        /// or record an offline replay tip (optionally marking the chat dirty for persist).
        /// </summary>
        Task ApplyIncomingEnvelopePreviewAsync(IncomingEnvelopePreviewRequest request);

        /// <summary>
        /// After journal recovery flush: re-tip only the chats that got recovered messages.
        /// </summary>
        Task ApplyIncomingJournalRecoveryPreviewsAsync(
            IEnumerable<KeyValuePair<string, ChatMessage>> latestByChat);

        /// <summary>
        /// Applies every waiting offline replay tip to the list. A failure puts them back and rethrows.
        /// </summary>
        Task FlushOfflineReplayTipsAsync(string reason);

        /// <summary>Updates list rows for one chat tip (duplicate/revoke/replay refresh).</summary>
        Task RefreshChatPreviewAsync(
            string chatJid,
            string previewText,
            DateTime timestamp,
            bool isFromMe,
            ChatPreviewKind? kindHint = null,
            string authorPrefix = null,
            string participantJid = null,
            string senderName = null);

        /// <summary>Re-tips every open row from the in-memory message cache.</summary>
        Task RefreshAllChatPreviewsFromStoredAsync(string reason);

        /// <summary>Creates missing rows and re-tips/reorders from the in-memory message cache.</summary>
        Task ReconcileChatListFromStoredAsync(string reason);

        /// <summary>
        /// Collapses rows that are the same person and re-keys in-memory timelines to the
        /// canonical address. Fire-and-forget from the list; the fold itself is awaited by
        /// callers that need the list settled first.
        /// </summary>
        void RequestChatListDedup(string reason);

        /// <summary>
        /// Same fold as <see cref="RequestChatListDedup"/>, awaited when startup / pump need
        /// the list settled before the next step.
        /// </summary>
        Task DeduplicateChatListAsync(string reason);

        /// <summary>
        /// Collapses a transient direct chat (often a LID self-address) into its canonical row:
        /// timeline, list, name maps, then durable identity / message cleanup.
        /// </summary>
        Task CollapseTransientDirectChatAsync(
            string transientJid,
            string canonicalJid,
            string reason);

        /// <summary>
        /// After a local send: tip every alias row (create if missing), then request dedupe.
        /// </summary>
        Task ShowLocalSendPreviewAsync(
            string chatJid,
            string preview,
            DateTime timestamp,
            ChatPreviewKind? kindHint = null,
            IList<string> mentionedJids = null,
            string messageId = null,
            string status = null);

        /// <summary>
        /// Startup repair for pre-v5.14 revoke envelopes stored as a fresh "[Message Deleted]" tip.
        /// </summary>
        Task RepairLegacyDeletedPreviewsAsync();

        /// <summary>
        /// After offline replay gates: reconcile list tips from SQLite, and for large desktop
        /// drains also repair/refresh from the in-memory cache. Host owns quiet/memory/catch-up.
        /// </summary>
        Task NotifyPostReplayMaintenanceAsync(
            int offlineCount,
            bool runDelayedBulkRepair,
            System.Threading.CancellationToken cancellationToken);

        /// <summary>
        /// Deferred-startup list pass: dedupe, legacy deleted-preview repair, SQLite tip
        /// reconcile (coalesced with a recent post-replay full walk unless the list changed).
        /// Host owns quiet/memory gates and contact-name hydrate before this.
        /// </summary>
        Task NotifyDeferredStartupListRepairAsync();

        /// <summary>
        /// Applies one history-sync preview slice onto the in-memory list: create missing rows,
        /// update tips when newer, apply pin/mute flags into the row and durable store.
        /// Must run on the UI thread (list mutations). Presentation batching stays with the caller.
        /// </summary>
        HistoryChatPreviewHydrateResult ApplyHistoryChatPreviewHydrate(
            HistoryChatPreviewHydrateRequest request);

        /// <summary>
        /// Corrects the chat's list strip from the timeline just opened, then asks the store for a
        /// reconcile in case SQLite holds something newer. Must run on the UI thread.
        /// </summary>
        void SyncListStripFromTimeline(
            ChatItem chat,
            string requestedJid,
            IReadOnlyList<ChatMessage> visibleMessages);

        /// <summary>
        /// When the row's exact JID cache has no pin/mute, copies those flags from PN/LID alias
        /// store keys onto the row (and remembers pin under every alias).
        /// </summary>
        void ApplyLocalStateAliasFallback(ChatItem chat);

        /// <summary>Caches the account pin under every PN/LID spelling of <paramref name="jid"/>.</summary>
        void RememberPinnedUnderAliases(string jid, bool pinned);

        /// <summary>Unix-seconds mute deadline eight hours from now (UTC).</summary>
        long MuteUntilEightHoursFromNow();

        /// <summary>Unix-seconds mute deadline one week from now (UTC).</summary>
        long MuteUntilOneWeekFromNow();

        /// <summary>Sentinel unix seconds for "mute forever" (year 2999 UTC).</summary>
        long MuteForeverUnixSeconds { get; }

        /// <summary>
        /// Confirms with the user, then deletes the conversation for the account everywhere.
        /// Returns true only when the delete ran (not cancelled / not refused).
        /// </summary>
        Task<bool> ConfirmAndDeleteAsync(ChatItem chat);

        /// <summary>Pin / timestamp / name order for the conversation list.</summary>
        List<ChatItem> SortChatsForDisplay(IEnumerable<ChatItem> source);

        /// <summary>
        /// One row per person while the same conversation may exist under PN and LID.
        /// </summary>
        List<ChatItem> DeduplicateChatsByCanonicalJid(
            IEnumerable<ChatItem> source,
            Func<string, string> getCanonical);

        /// <summary>
        /// Collapses linked subgroups under a community hub row for the chat list.
        /// </summary>
        List<ChatItem> ProjectCommunityList(
            IReadOnlyList<ChatItem> source,
            Func<string, ChatItem> resolveParent,
            Func<string, ChatItem> getOrCreateSynthetic,
            Func<string, string> getCanonical);

        /// <summary>
        /// Whether a list row matches the search box (title, strip, JID, mention labels).
        /// </summary>
        bool MatchesChatListQuery(
            ChatItem chat,
            string query,
            Func<string> resolveChatName,
            Func<string, string> resolveMentionName);

        /// <summary>
        /// Cold toast/tile may hold the chat list until the conversation loads; warm activation must not.
        /// </summary>
        bool ShouldArmDeepLinkListHold(bool appWasAlreadyOpen);

        /// <summary>Hide SyncStatusPanel / status-bar sync line while a cold deep-link hold is active.</summary>
        bool ShouldSuppressDeepLinkSyncBanner(bool deepLinkHeld);

        /// <summary>
        /// Prefer ChatList_Loading over sync-progress copy on the full-list overlay (cold toast only).
        /// </summary>
        bool ShouldPreferDeepLinkLoadingOverlay(bool deepLinkHeld, bool listAlreadyMaterialized);

        /// <summary>
        /// After mass display-name apply, rebuild VisibleChats only when search membership can change.
        /// </summary>
        bool RequiresNamesAppliedVisibleRebuild(string searchQuery);

        /// <summary>
        /// Which <see cref="ChatItem"/> property change can move a row (pin / recency / community link).
        /// </summary>
        bool RequiresVisibleReorder(string propertyName);

        /// <summary>
        /// True when the chat-list control must not be created yet (first history sync or cold deep-link).
        /// </summary>
        bool ShouldHoldChatListRows(
            bool isAwaitingFirstHistorySync,
            bool deepLinkDetailFirst,
            bool listAlreadyMaterialized);

        /// <summary>
        /// True while sync status is still enriching the list (names / avatars / groups / settling / low-memory).
        /// </summary>
        bool MatchesListEnrichmentPhase(string syncStatus);
    }
}
