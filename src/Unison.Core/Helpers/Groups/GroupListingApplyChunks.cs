// =============================================================================
// GroupListingApplyChunks
//
// PreferFrugal journal 089b69a2: one sync hop for subjects+hubs held the UI
// ~40s (ApplyListingAsync). Cache / hub ensure / flag paint must slice.
// =============================================================================
using System;

namespace Unison.Core.Helpers.Groups
{
    public static class GroupListingApplyChunks
    {
        public const int FlagPaintChunk = 24;

        /// <summary>
        /// Frugal subjects were 16 and still produced 19s Low hops on Lumia (journal
        /// 8b7b5612). Smaller slices yield the Low lane so chat-open / OnDemand tips
        /// on Normal can run between hops.
        /// </summary>
        public static int SubjectCacheChunk(bool preferFrugal) =>
            preferFrugal ? 8 : FlagPaintChunk;

        /// <summary>Hub Adds are the expensive part (CollectionChanged → list refresh).</summary>
        public static int HubEnsureChunk(bool preferFrugal) =>
            preferFrugal ? 8 : 12;

        public static TimeSpan InterChunkDelay(bool preferFrugal) =>
            TimeSpan.FromMilliseconds(preferFrugal ? 48 : 16);
    }
}
