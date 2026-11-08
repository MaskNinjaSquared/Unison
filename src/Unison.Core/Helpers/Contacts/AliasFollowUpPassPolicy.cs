// =============================================================================
// AliasFollowUpPassPolicy
//
// Cap'd avatar drains used to run PersistAliases + ChatListDeduper on every
// 48-target slice (journal: skip-unchanged every ~750ms for minutes). Deduper
// only answers once the avatar remainder is empty.
//
// PreferFrugal more=True rearm at 750ms still saturated Normal (ea23bb7b) —
// space avatar passes further apart while tips own the list.
// =============================================================================
using System;

namespace Unison.Core.Helpers.Contacts
{
    public static class AliasFollowUpPassPolicy
    {
        public static readonly TimeSpan DefaultRearmDebounce = TimeSpan.FromMilliseconds(750);

        public static readonly TimeSpan FrugalMoreRearmDebounce = TimeSpan.FromMilliseconds(2500);

        /// <summary>
        /// Mid-drain slices only paint avatars; fold/dedupe waits for the last slice.
        /// </summary>
        public static bool ShouldRunDeduper(bool hasMoreAvatarTargets) =>
            !hasMoreAvatarTargets;

        /// <summary>
        /// Re-arm the debounce only when avatar targets are still owed — a bare
        /// Request without targets must not spin empty Deduper passes.
        /// </summary>
        public static bool ShouldRearmAfterPass(bool hasPendingAvatarTargets) =>
            hasPendingAvatarTargets;

        /// <summary>
        /// Extra wait before Schedule when PreferFrugal still owes avatar targets.
        /// </summary>
        public static TimeSpan RearmGapBeforeSchedule(bool preferFrugal, bool hasMoreAvatarTargets) =>
            preferFrugal && hasMoreAvatarTargets
                ? FrugalMoreRearmDebounce
                : TimeSpan.Zero;
    }
}
