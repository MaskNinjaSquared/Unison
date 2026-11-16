// =============================================================================
// HistoryListUiRefresh
//
// When a history chunk should (not) rebuild the painted chat list. OnDemand and
// tip hydrate are tip/timeline work — RefreshVisibleChats / in-place reorder
// after chat-open stole Mobile frames (container-changing storms under detail).
// =============================================================================
using System;

namespace Unison.Core.Helpers.Chats
{
    public static class HistoryListUiRefresh
    {
        public static bool IsOnDemandSyncType(string syncType)
        {
            return !string.IsNullOrEmpty(syncType) &&
                   syncType.IndexOf("OnDemand", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Non-null OnDemand must not call RefreshVisibleChats. Tips land via
        /// MessageChunkPersisted / preview hydrate; the open chat merges via
        /// ChatMessagesChanged.
        /// </summary>
        public static bool ShouldRefreshVisibleChatsOnHistorySync(bool syncIsOnDemand) =>
            ShouldRefreshVisibleChatsOnHistorySync(syncIsOnDemand, listAlreadyMaterialized: false);

        /// <summary>
        /// After the first unlock, Full/Recent <c>HistorySyncReceived</c> must not rebuild
        /// Visible on every chunk — that flickered the list while tips still arrived via
        /// hydrate. First paint stays on InitialSyncProgress → batch release.
        /// </summary>
        public static bool ShouldRefreshVisibleChatsOnHistorySync(
            bool syncIsOnDemand,
            bool listAlreadyMaterialized)
        {
            if (syncIsOnDemand || listAlreadyMaterialized)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Sqlite OnDemand apply must not raise a null HistorySyncReceived — that
        /// path still RefreshVisibleChats when the list is already painted.
        /// </summary>
        public static bool ShouldRaiseNullHistorySyncReceived(bool syncIsOnDemand) =>
            !syncIsOnDemand;

        /// <summary>
        /// Tip-only hydrate reorder Moves the ObservableCollection; while chat-open
        /// owns the timeline gate the list must stay still.
        /// </summary>
        public static bool ShouldReorderVisibleOnTipHydrate(bool timelineLoadPriorityHeld) =>
            ShouldReorderVisibleOnTipHydrate(
                timelineLoadPriorityHeld,
                batchOrSafeModeSettling: false,
                hydrateQueueStillPending: false);

        /// <summary>
        /// Join tip-hydrate slices (and batch/safe-mode settle) into one order pass —
        /// same idea as offline <c>DeferTipPaintUntilSettle</c>, for the painted list.
        /// </summary>
        public static bool ShouldReorderVisibleOnTipHydrate(
            bool timelineLoadPriorityHeld,
            bool batchOrSafeModeSettling,
            bool hydrateQueueStillPending)
        {
            if (timelineLoadPriorityHeld || batchOrSafeModeSettling || hydrateQueueStillPending)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// OnDemand fills older SQLite pages for scroll-up. ChatMessagesChanged →
        /// LoadRecentMessagesForSyncAsync only merges the newest tail and, after a LID
        /// burst, can delete/reinsert that zone on the open timeline (Mobile freeze,
        /// watchdog active=none on raw Dispatcher.RunAsync).
        /// </summary>
        public static bool ShouldNotifyChatMessagesChangedAfterChunk(bool syncIsOnDemand) =>
            !syncIsOnDemand;

        /// <summary>
        /// Session-open OnDemand is claimed before the phone page lands so tip-only /
        /// thin open chats can absorb the SQLite page once. Scroll-up OnDemand stays
        /// silent (claim already consumed).
        /// </summary>
        public static bool ShouldNotifyChatMessagesChangedForJid(
            bool syncIsOnDemand,
            bool sessionOpenOnDemandClaimed)
        {
            if (!syncIsOnDemand)
            {
                return true;
            }

            return sessionOpenOnDemandClaimed;
        }

        /// <summary>
        /// Tip-only / thin open timelines cannot Merge older OnDemand rows (insert
        /// skips older-than-tip). Replace the window from a full open page instead.
        /// </summary>
        public static bool ShouldReplaceTimelineOnHistorySync(
            int visibleMessageCount,
            int initialUiMessageWindow)
        {
            if (initialUiMessageWindow <= 0)
            {
                return visibleMessageCount <= 1;
            }

            return visibleMessageCount < initialUiMessageWindow;
        }
    }
}
