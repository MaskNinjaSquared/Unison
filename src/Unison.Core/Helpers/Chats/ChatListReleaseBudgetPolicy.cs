// =============================================================================
// ChatListReleaseBudgetPolicy
//
// One release tick adds a first batch unconditionally, then keeps adding while
// the add work itself stays under the per-turn budget. The clock starts at the
// first add: skipping rows already on screen is O(visible) and, timed from the
// top, ate the whole budget so every tick released only the first batch
// (b31 fb8e90ad: 437 rows in 51 ticks of ~400ms on Low).
// =============================================================================

namespace Unison.Core.Helpers.Chats
{
    public static class ChatListReleaseBudgetPolicy
    {
        public static bool CanAddMore(int added, int batchSize, long addElapsedMs, int budgetMs) =>
            added < batchSize || addElapsedMs < budgetMs;

        /// <param name="addStartedTicks">0 while nothing has been added this tick.</param>
        public static long AddElapsedMs(long addStartedTicks, long nowTicks, long ticksPerMs) =>
            addStartedTicks == 0 || ticksPerMs <= 0
                ? 0
                : (nowTicks - addStartedTicks) / ticksPerMs;
    }
}
