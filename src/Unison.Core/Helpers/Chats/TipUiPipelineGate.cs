// =============================================================================
// TipUiPipelineGate
//
// Catch-up UI order: tip paint first, silent work (alias drain, listing, full
// heal) only when the offline tip book is idle. Journal f2c3cb61: deferred-startup
// forceFull after one dedupe merge re-walked 270 tips; alias more=True still
// competed with ApplyOfflineReplay on the same dispatcher.
//
// Journal ea23bb7b (2.4.36): IsBusy cleared between progressive flushes; alias
// more=True began every ~750ms and ApplyOfflineReplay waited 8–25s on Normal.
// Post-flush quiet keeps silent UI out of those gaps.
// =============================================================================
using System;

namespace Unison.Core.Helpers.Chats
{
    public static class TipUiPipelineGate
    {
        /// <summary>
        /// After a tip flush ends, keep the tip-busy gate closed so alias/listing
        /// cannot start in the empty-book gap before the next progressive Record.
        /// </summary>
        public static TimeSpan PostFlushQuiet(bool preferFrugal) =>
            preferFrugal
                ? TimeSpan.FromMilliseconds(2000)
                : TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Silent UI (alias follow-up run, listing apply, name refresh) may proceed
        /// only when offline tip flush is not owning the list (including post-flush quiet).
        /// </summary>
        public static bool AllowSilentUi(bool tipBusy) => !tipBusy;

        /// <summary>
        /// PreferFrugal silent UI (name refresh, alias avatar collect) must use Low so
        /// Normal tip paint / softOrder are not starved — journal 82908f3f: ChatNameRefresh
        /// on Normal held ~44s and post-replay Low slices waited 18–86s with swapped=0.
        /// </summary>
        public static bool PreferLowSilentUi(bool preferFrugal) => preferFrugal;

        /// <summary>
        /// Deferred-startup must not force a second full tip walk for a mere
        /// dedupe merge — cool-down already covers that. Legacy tip repair rewrote
        /// strips and still needs a forced walk.
        /// </summary>
        public static bool ShouldForceDeferredStartupFullHeal(bool deduped, bool repaired) =>
            repaired;
    }
}
