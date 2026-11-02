// =============================================================================
// CallFavoritePreview
//
// Pure Favourites slice for the Calls tab (cap + "see all").
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Unison.Core.Contracts;
using Unison.Core.Models;

namespace Unison.Core.Helpers.Calls
{
    public static class CallFavoritePreview
    {
        public const int DefaultCap = 5;

        public static IReadOnlyList<ChatItem> SelectOrdered(
            IEnumerable<ChatItem> chats,
            IStringResources strings,
            int cap,
            bool showAll,
            out bool hasMore)
        {
            List<ChatItem> favorites = (chats ?? Array.Empty<ChatItem>())
                .Where(c => c != null && c.IsFavorite)
                .OrderBy(
                    c => c.GetNameResolved(strings) ?? c.Name ?? c.JID ?? string.Empty,
                    StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            hasMore = favorites.Count > cap;
            if (showAll || !hasMore)
            {
                return favorites;
            }

            return favorites.Take(cap).ToList();
        }
    }
}
