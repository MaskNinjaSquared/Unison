// =============================================================================
// HistoryOnDemandPacing
//
// Fixed (tier-independent) timings for FULL_HISTORY / ON_DEMAND requests and
// background hydration. Tier-dependent knobs live on IHistorySyncTuning /
// ISyncPhaseTuning (SyncProductPolicy), not here.
// =============================================================================
using System;

namespace Unison.WhatsAppClient.Helpers
{
    public static class HistoryOnDemandPacing
    {
        /// <summary>Per-chat ON_DEMAND request: give up waiting for the reply after this.</summary>
        public static readonly TimeSpan OnDemandResponseTimeout = TimeSpan.FromSeconds(20);

        /// <summary>FULL_HISTORY_SYNC_ON_DEMAND: the phone may take minutes to start sending lots.</summary>
        public static readonly TimeSpan FullHistoryResponseTimeout = TimeSpan.FromMinutes(15);

        public static readonly TimeSpan FullHistoryCatchUpProgressPoll = TimeSpan.FromSeconds(8);

        public static readonly TimeSpan FreshnessStaleThreshold = TimeSpan.FromMinutes(30);

        public static readonly TimeSpan BackfillStaleThreshold = TimeSpan.FromHours(8);

        public const int BackfillMaxStaleTopChats = 40;

        public static readonly TimeSpan HydrationSettleBudget = TimeSpan.FromSeconds(25);

        public const int HydrationBatchSize = 10;

        public const int HydrationPageSize = 50;

        public const int HydrationMaxPagesPerChat = 5;
    }
}
