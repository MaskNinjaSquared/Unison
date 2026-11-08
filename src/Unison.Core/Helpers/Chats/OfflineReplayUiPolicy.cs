// =============================================================================
// OfflineReplayUiPolicy
//
// Progressive tip apply already merges latest-wins in the book. On PreferFrugal
// devices, catalogue Reposition per tip still storms CollectionChanged.Move →
// move-miss. Skip those Moves and let the list VM drain Visible in place once
// (softOrder). Soft-order notify must leave the ApplyOffline UI hop first —
// nesting repair under HasThreadAccess billed 20–30s Moves as ApplyOffline
// (82908f3f).
//
// Progressive tip heal re-reads SQLite for tips just painted from the summary
// (ea23bb7b: offline-tip-heal often swapped=0 while still occupying Normal for
// hundreds of ms under alias load). PreferFrugal skips heal until settle.
//
// PreferFrugal also defers all tip UI until settle (persist → quiet → one chunked
// paint). Progressive mid-replay paint was the open-app freeze: list hops every
// ~1s while the pump still drained offline.
// =============================================================================
using System;

namespace Unison.Core.Helpers.Chats
{
    public static class OfflineReplayUiPolicy
    {
        public const string ProgressiveReason = "replay-progressive";

        /// <summary>
        /// True when ApplyOfflineReplay should not Reposition the catalogue after tip paint.
        /// </summary>
        public static bool SkipCatalogueReposition(bool preferFrugal) => preferFrugal;

        /// <summary>
        /// PreferFrugal: keep tips in the book only; paint once on settle
        /// (<c>offline-complete</c> / explicit flush), not on the progressive timer.
        /// </summary>
        public static bool DeferTipPaintUntilSettle(bool preferFrugal) => preferFrugal;

        /// <summary>
        /// PreferFrugal progressive flushes already painted the tip; settle still heals.
        /// Kept for callers that still pass <see cref="ProgressiveReason"/> after defer.
        /// </summary>
        public static bool SkipProgressiveTipHeal(bool preferFrugal, string reason) =>
            preferFrugal &&
            string.Equals(reason, ProgressiveReason, StringComparison.Ordinal);
    }
}
