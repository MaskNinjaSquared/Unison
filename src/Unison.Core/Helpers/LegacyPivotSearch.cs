using System;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Which Legacy pivot can take the search box. Only the chat lists have a query today;
    /// calls, favorites and status stay put until they grow one.
    /// </summary>
    public static class LegacyPivotSearch
    {
        public static bool AppliesToChatList(string pivotTag)
        {
            return string.Equals(pivotTag, "chats", StringComparison.OrdinalIgnoreCase)
                || string.Equals(pivotTag, "archived", StringComparison.OrdinalIgnoreCase);
        }
    }
}
