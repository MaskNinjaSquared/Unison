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
// PreferFrugal defers tip UI only while the offline replay drain is active
// (persist → quiet → one chunked paint). Mid-replay progressive was the open-app
// freeze. After settle, late Append must not keep the offline-tip gate busy via
// backstop+quiet (7a57ffb9): progressive / live tip paths take over.
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
        /// PreferFrugal + active replay drain: keep tips in the book only; paint once on
        /// settle (<c>offline-complete</c> / explicit flush), not on the progressive timer.
        /// After drain ends, late Append paints progressively / live.
        /// </summary>
        public static bool DeferTipPaintUntilSettle(bool preferFrugal, bool replayDrainActive) =>
            preferFrugal && replayDrainActive;

        /// <summary>
        /// Post-flush IsBusy quiet. Zero after PreferFrugal settle so SilentNames /
        /// GroupListing are not parked by every late Append tip flush.
        /// </summary>
        public static TimeSpan TipBusyQuietAfterFlush(bool preferFrugal, bool replayDrainActive)
        {
            if (preferFrugal && !replayDrainActive)
            {
                return TimeSpan.Zero;
            }

            return TipUiPipelineGate.PostFlushQuiet(preferFrugal);
        }

        /// <summary>
        /// Wire Append is offline backlog; send echo also uses Append but must take the
        /// live tip path (not the offline tip book).
        /// </summary>
        public static bool IsOfflineEnvelope(bool upsertIsAppend, bool fromMe) =>
            upsertIsAppend && !fromMe;

        /// <summary>
        /// PreferFrugal progressive flushes already painted the tip; settle still heals.
        /// Kept for callers that still pass <see cref="ProgressiveReason"/> after defer.
        /// </summary>
        public static bool SkipProgressiveTipHeal(bool preferFrugal, string reason) =>
            preferFrugal &&
            string.Equals(reason, ProgressiveReason, StringComparison.Ordinal);
    }
}
