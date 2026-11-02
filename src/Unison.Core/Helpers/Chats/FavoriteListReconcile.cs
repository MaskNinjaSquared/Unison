using System;
using System.Collections.Generic;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Pure membership helpers for WhatsApp Favourites (app-state <c>favoritesAction</c>).
    /// </summary>
    public static class FavoriteListReconcile
    {
        /// <summary>
        /// Local favourite JIDs that are absent from the remote wanted set (cold clears).
        /// </summary>
        public static IReadOnlyList<string> ColdFavoritesToClear(
            IEnumerable<string> localFavoriteJids,
            ISet<string> wantedCanonical,
            Func<string, string> toCanonical)
        {
            if (localFavoriteJids == null || wantedCanonical == null || toCanonical == null)
            {
                return Array.Empty<string>();
            }

            var clear = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in localFavoriteJids)
            {
                string canonical = toCanonical(raw);
                if (string.IsNullOrWhiteSpace(canonical) ||
                    !seen.Add(canonical) ||
                    wantedCanonical.Contains(canonical))
                {
                    continue;
                }

                clear.Add(canonical);
            }

            return clear;
        }
    }
}
