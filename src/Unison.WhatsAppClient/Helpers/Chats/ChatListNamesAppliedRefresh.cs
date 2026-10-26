// =============================================================================
// ChatListNamesAppliedRefresh
//
// After a mass display-name apply, row titles repaint through ChatItem.Name
// bindings. Rebuilding VisibleChats only matters when search membership can
// change because a newly resolved title now matches (or stops matching) the query.
// =============================================================================
namespace Unison.WhatsAppClient.Helpers
{
    public static class ChatListNamesAppliedRefresh
    {
        public static bool RequiresVisibleRebuild(string searchQuery)
        {
            return !string.IsNullOrWhiteSpace(searchQuery);
        }
    }
}
