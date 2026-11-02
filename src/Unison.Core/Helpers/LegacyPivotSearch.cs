using System;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Which Legacy pivot can take the search box. Chat lists and Favourites filter in-memory;
    /// calls and status stay put until they grow a query.
    /// </summary>
    public static class LegacyPivotSearch
    {
        public static bool AppliesToChatList(string pivotTag)
        {
            return string.Equals(pivotTag, "chats", StringComparison.OrdinalIgnoreCase)
                || string.Equals(pivotTag, "archived", StringComparison.OrdinalIgnoreCase);
        }

        public static bool AppliesToFavorites(string pivotTag)
        {
            return string.Equals(pivotTag, "favorites", StringComparison.OrdinalIgnoreCase);
        }

        public static bool AppliesToSearchablePivot(string pivotTag)
        {
            return AppliesToChatList(pivotTag) || AppliesToFavorites(pivotTag);
        }
    }
}
