// =============================================================================
// ChatListIncrementalMove
//
// When the catalogue Moves a row, VisibleChats may not hold that instance:
// community children are projected under a hub, and PN/LID can leave a secondary
// row in Chats. Treating every miss as a full BuildVisibleRefreshSource is what
// froze Mobile during offline SortInPlace (run 79a6e7ce: move-miss storms →
// buildMs hundreds–17000).
// =============================================================================
using Unison.Core.Models;

namespace Unison.Core.Helpers.Chats
{
    public static class ChatListIncrementalMove
    {
        /// <summary>
        /// Catalogue Move for a linked subgroup that is not painted on the list —
        /// absorb without scheduling a full visible rebuild.
        /// </summary>
        public static bool ShouldAbsorbHiddenCatalogueMove(ChatItem catalogueRow, bool hasVisibleRow)
        {
            if (hasVisibleRow || catalogueRow == null)
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(catalogueRow.LinkedParentJid);
        }

        /// <summary>
        /// Linked children need CommunityListProjector (hide under hub). Hubs already on
        /// Visible can Move like any tip row — their strip timestamp drives order.
        /// </summary>
        public static bool RequiresCommunityProjectionRebuild(ChatItem visible, ChatItem catalogue)
        {
            if (!string.IsNullOrWhiteSpace(visible?.LinkedParentJid) ||
                !string.IsNullOrWhiteSpace(catalogue?.LinkedParentJid))
            {
                // Visible child should not happen after project; force rebuild if it does.
                // Hidden catalogue child is handled by ShouldAbsorbHiddenCatalogueMove.
                return visible != null;
            }

            return false;
        }
    }
}
