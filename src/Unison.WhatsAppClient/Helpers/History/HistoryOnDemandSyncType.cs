// =============================================================================
// HistoryOnDemandSyncType
//
// Whether a history syncType string is an on-demand pull (name contains
// "OnDemand") rather than the initial bulk history dump.
// =============================================================================
using Unison.Core.Helpers.Chats;

namespace Unison.WhatsAppClient.Helpers
{
    public static class HistoryOnDemandSyncType
    {
        public static bool Matches(string syncType) =>
            HistoryListUiRefresh.IsOnDemandSyncType(syncType);
    }
}
