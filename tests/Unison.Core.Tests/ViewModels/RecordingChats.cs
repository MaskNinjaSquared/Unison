// =============================================================================
// Records StartNewChat / StartNewCommunity for create-flow tests. Other IChatService members are
// unused by NewConversationViewModel and answer with empty defaults.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;

namespace Unison.Core.Tests.ViewModels
{
    internal sealed class RecordingChats : IChatService
    {
        public string LastStartJid { get; private set; }

        public string LastStartDisplayName { get; private set; }

        public event EventHandler FavoritesMembershipChanged
        {
            add { }
            remove { }
        }

        public event EventHandler VisibleOrderInvalidated
        {
            add { }
            remove { }
        }

        public void StartNewChat(string jid, string displayName = null)
        {
            LastStartJid = jid;
            LastStartDisplayName = displayName;
        }

        public string LastCommunityJid { get; private set; }

        public string LastCommunityDisplayName { get; private set; }

        public void StartNewCommunity(string parentJid, string displayName)
        {
            LastCommunityJid = parentJid;
            LastCommunityDisplayName = displayName;
        }

        public Task SetPinnedAsync(ChatItem chat, bool pinned) => Task.CompletedTask;

        public Task SetFavoriteAsync(ChatItem chat, bool favorite) => Task.CompletedTask;

        public Task MarkReadAsync(ChatItem chat) => Task.CompletedTask;

        public Task EnsurePendingMarkReadReceiptsAsync(ChatItem chat) => Task.CompletedTask;

        public Task CommitInboundHistoryReadAsync(ChatItem chat) => Task.CompletedTask;

        public Task DeleteChatAsync(ChatItem chat) => Task.CompletedTask;

        public Task ApplyAppStateDeleteChatAsync(string jid) => Task.CompletedTask;

        public void SetActiveChatJid(string jid) { }

        public Task ClearUnreadForChatAsync(string jid) => Task.CompletedTask;

        public int GetTotalUnreadCount() => 0;

        public void PersistChatListRows(IList<ChatItem> chats) { }

        public Task ReconcileChatPreviewsFromSqliteAsync(
            IReadOnlyList<string> chatJids = null,
            string reason = null) =>
            Task.CompletedTask;

        public Task<LiveIncomingChatListApplyResult> ApplyLiveIncomingChatListAsync(
            LiveIncomingChatListApplyRequest request) =>
            Task.FromResult<LiveIncomingChatListApplyResult>(null);

        public void RecordOfflineReplayTip(OfflineReplayTipRequest request) { }

        public Task ApplyIncomingEnvelopePreviewAsync(IncomingEnvelopePreviewRequest request) =>
            Task.CompletedTask;

        public Task ApplyIncomingJournalRecoveryPreviewsAsync(
            IEnumerable<KeyValuePair<string, ChatMessage>> latestByChat) =>
            Task.CompletedTask;

        public Task FlushOfflineReplayTipsAsync(string reason) => Task.CompletedTask;

        public bool IsOfflineReplayTipBusy => false;

        public Task RefreshChatPreviewAsync(
            string chatJid,
            string previewText,
            DateTime timestamp,
            bool isFromMe,
            ChatPreviewKind? kindHint = null,
            string authorPrefix = null,
            string participantJid = null,
            string senderName = null) =>
            Task.CompletedTask;

        public Task RefreshAllChatPreviewsFromStoredAsync(string reason) => Task.CompletedTask;

        public Task ReconcileChatListFromStoredAsync(string reason) => Task.CompletedTask;

        public void RequestChatListDedup(string reason) { }

        public Task DeduplicateChatListAsync(string reason) => Task.CompletedTask;

        public Task CollapseTransientDirectChatAsync(
            string transientJid,
            string canonicalJid,
            string reason) =>
            Task.CompletedTask;

        public Task ShowLocalSendPreviewAsync(
            string chatJid,
            string preview,
            DateTime timestamp,
            ChatPreviewKind? kindHint = null,
            IList<string> mentionedJids = null,
            string messageId = null,
            string status = null) =>
            Task.CompletedTask;

        public Task RepairLegacyDeletedPreviewsAsync() => Task.CompletedTask;

        public Task NotifyPostReplayMaintenanceAsync(
            int offlineCount,
            bool runDelayedBulkRepair,
            System.Threading.CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task NotifyDeferredStartupListRepairAsync() => Task.CompletedTask;

        public HistoryChatPreviewHydrateResult ApplyHistoryChatPreviewHydrate(
            HistoryChatPreviewHydrateRequest request) =>
            null;

        public void SyncListStripFromTimeline(
            ChatItem chat,
            string requestedJid,
            IReadOnlyList<ChatMessage> visibleMessages) { }

        public void ApplyLocalStateAliasFallback(ChatItem chat) { }

        public void RememberPinnedUnderAliases(string jid, bool pinned, long? pinnedTimestamp = null) { }

        public long MuteUntilEightHoursFromNow() => 0;

        public long MuteUntilOneWeekFromNow() => 0;

        public long MuteForeverUnixSeconds => 0;

        public Task<bool> ConfirmAndDeleteAsync(ChatItem chat) => Task.FromResult(false);

        public List<ChatItem> SortChatsForDisplay(IEnumerable<ChatItem> source) =>
            source != null ? new List<ChatItem>(source) : new List<ChatItem>();

        public List<ChatItem> DeduplicateChatsByCanonicalJid(
            IEnumerable<ChatItem> source,
            Func<string, string> getCanonical) =>
            source != null ? new List<ChatItem>(source) : new List<ChatItem>();

        public List<ChatItem> ProjectCommunityList(
            IReadOnlyList<ChatItem> source,
            Func<string, ChatItem> resolveParent,
            Func<string, ChatItem> getOrCreateSynthetic,
            Func<string, string> getCanonical) =>
            source != null ? new List<ChatItem>(source) : new List<ChatItem>();

        public bool MatchesChatListQuery(
            ChatItem chat,
            string query,
            Func<string> resolveChatName,
            Func<string, string> resolveMentionName) =>
            false;

        public bool ShouldArmDeepLinkListHold(bool appWasAlreadyOpen) => false;

        public bool ShouldSuppressDeepLinkSyncBanner(bool deepLinkHeld) => false;

        public bool ShouldPreferDeepLinkLoadingOverlay(bool deepLinkHeld, bool listAlreadyMaterialized) =>
            false;

        public bool RequiresNamesAppliedVisibleRebuild(string searchQuery) => false;

        public bool RequiresVisibleReorder(string propertyName) => false;

        public bool ShouldHoldChatListRows(
            bool isAwaitingFirstHistorySync,
            bool deepLinkDetailFirst,
            bool listAlreadyMaterialized) =>
            false;

        public bool MatchesListEnrichmentPhase(string syncStatus) => false;

        public void ClearInMemoryTimelines() { }
    }
}
