using System;
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Files a cache clean will remove. Already-deleted rows never arrive here.
    /// The same path referenced twice is deleted once.
    /// </summary>
    public static class MediaCacheCleanPlan
    {
        public static IReadOnlyList<string> DistinctUris(IEnumerable<CachedMediaEntry> entries)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();
            if (entries == null)
            {
                return list;
            }

            foreach (CachedMediaEntry entry in entries)
            {
                Add(seen, list, entry?.LocalUri);
                Add(seen, list, entry?.PosterUri);
            }

            return list;
        }

        private static void Add(HashSet<string> seen, List<string> list, string uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
            {
                return;
            }

            if (seen.Add(uri))
            {
                list.Add(uri);
            }
        }
    }
}
