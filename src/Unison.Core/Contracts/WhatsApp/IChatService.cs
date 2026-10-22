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
        /// Marks everything in the conversation as read: the senders are told, and the unread
        /// badge is cleared here and on the phone. Does nothing when there was nothing unread,
        /// so it is safe to call every time a chat is opened.
        /// </summary>
        Task MarkReadAsync(ChatItem chat);

        /// <summary>
        /// Deletes the conversation for this account everywhere: it leaves the list here, on the
        /// phone and on every other linked device. The messages are removed locally too - unlike a
        /// pin there is nothing to revert to, so this is not undoable and callers are expected to
        /// have asked first.
        /// </summary>
        Task DeleteChatAsync(ChatItem chat);

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
    }
}
