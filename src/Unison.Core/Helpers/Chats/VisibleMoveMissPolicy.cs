// =============================================================================
// VisibleMoveMissPolicy
//
// PreferFrugal Move-miss (and tip-driven order dirt) used to ScheduleRefresh →
// BuildVisibleRefreshSource. When the painted list is already the unfiltered
// catalogue, reorder in place instead — same end state, no rebuild storm.
// =============================================================================
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers.Chats
{
    public static class VisibleMoveMissPolicy
    {
        /// <summary>
        /// True when frugal UI should drain/reorder VisibleChats instead of rebuilding.
        /// </summary>
        public static bool PreferInPlaceReorder(
            bool preferFrugal,
            int visibleCount,
            bool filterIsAll,
            bool searchEmpty)
        {
            return preferFrugal &&
                visibleCount > 0 &&
                filterIsAll &&
                searchEmpty;
        }

        /// <summary>
        /// Subgroups that already have <see cref="ChatItem.LinkedParentJid"/> must not stay
        /// on the main list; prune them under the hub instead of a full catalogue rebuild.
        /// </summary>
        public static bool HasLinkedChildrenOnRoot(IReadOnlyList<ChatItem> visible)
        {
            if (visible == null || visible.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < visible.Count; i++)
            {
                ChatItem chat = visible[i];
                if (chat != null && !string.IsNullOrWhiteSpace(chat.LinkedParentJid))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
