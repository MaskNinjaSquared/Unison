// =============================================================================
// HistoryOnDemandSyncType
//
// Whether a history syncType string is an on-demand pull (name contains
// "OnDemand") rather than the initial bulk history dump.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public static class HistoryOnDemandSyncType
    {
        public static bool Matches(string syncType)
        {
            return !string.IsNullOrEmpty(syncType) &&
                   syncType.IndexOf("OnDemand", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
