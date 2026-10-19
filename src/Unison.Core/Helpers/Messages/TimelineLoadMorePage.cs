// =============================================================================
// TimelineLoadMorePage
//
// When SQLite returns a page whose ids are already on screen (cursor overlap —
// common on self-chat PN/LID dual keys), the UI must advance past that page
// instead of treating it as success with zero prepends (stuck scroll-up).
// =============================================================================
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class TimelineLoadMorePage
    {
        public static bool IsDuplicateOnlyPage(int returnedCount, int prependedCount) =>
            returnedCount > 0 && prependedCount == 0;

        /// <summary>
        /// Chronological page tip (oldest row) used to re-query past a duplicate-only page.
        /// </summary>
        public static ChatMessage OldestTip(IList<ChatMessage> chronologicalPage)
        {
            if (chronologicalPage == null || chronologicalPage.Count == 0)
            {
                return null;
            }

            return chronologicalPage[0];
        }
    }
}
