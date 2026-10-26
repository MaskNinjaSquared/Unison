// =============================================================================
// FullChatPreviewReconcileGate
//
// Decides when a second full-list tip reconcile is noise. Startup runs both
// post-replay and deferred-startup walks within seconds; walking ~every chat
// twice floods Mobile UI with tip-swapped PropertyChanged. Structural list
// changes (dedupe merge / legacy tip repair) always force a fresh walk.
// =============================================================================
using System;

namespace Unison.WhatsAppClient.Helpers
{
    public static class FullChatPreviewReconcileGate
    {
        /// <summary>
        /// Cool-down after a completed full reconcile before another optional full
        /// pass may run. Must cover quiet-wait gaps after <c>persisted-ui-loaded</c>
        /// (often 10–20s) so <c>deferred-startup</c> / <c>post-replay</c> do not
        /// paint every tip twice on Low Mobile.
        /// </summary>
        public static readonly TimeSpan DefaultCoolDown = TimeSpan.FromSeconds(90);

        /// <summary>
        /// Whether a second full-list tip walk can be skipped.
        /// </summary>
        /// <param name="lastFullCompletedUtc">
        /// When the previous full reconcile finished; <see cref="DateTime.MinValue"/>
        /// means none yet.
        /// </param>
        /// <param name="nowUtc">Clock for the decision.</param>
        /// <param name="coolDown">Skip window after <paramref name="lastFullCompletedUtc"/>.</param>
        /// <param name="listStructureChanged">
        /// True when dedupe merged rows or legacy tip repair rewrote strips — those
        /// invalidate the previous walk.
        /// </param>
        public static bool ShouldSkipDuplicateFull(
            DateTime lastFullCompletedUtc,
            DateTime nowUtc,
            TimeSpan coolDown,
            bool listStructureChanged)
        {
            if (listStructureChanged)
            {
                return false;
            }

            if (lastFullCompletedUtc == DateTime.MinValue)
            {
                return false;
            }

            if (coolDown <= TimeSpan.Zero)
            {
                return false;
            }

            return nowUtc - lastFullCompletedUtc < coolDown;
        }
    }
}
