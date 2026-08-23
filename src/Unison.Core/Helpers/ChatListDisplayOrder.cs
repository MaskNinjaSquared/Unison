using System;
using System.Collections.Generic;
using System.Linq;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Chat-list row ordering: PN/LID dedupe then pin / timestamp / name sort.
    /// </summary>
    public static class ChatListDisplayOrder
    {
        /// <summary>
        /// One row per person while the same conversation may exist under PN and LID.
        /// Newest preview wins; pin state is carried across aliases.
        /// </summary>
        public static List<ChatItem> DeduplicateByCanonicalJid(
            IEnumerable<ChatItem> source,
            Func<string, string> getCanonical)
        {
            var deduped = new List<ChatItem>();
            if (source == null || getCanonical == null)
            {
                return deduped;
            }

            var canonicalIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (ChatItem item in source)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.JID))
                {
                    continue;
                }

                string canonical = getCanonical(item.JID) ?? item.JID;
                if (!canonicalIndex.TryGetValue(canonical, out int existingIndex))
                {
                    canonicalIndex[canonical] = deduped.Count;
                    deduped.Add(item);
                    continue;
                }

                ChatItem existing = deduped[existingIndex];

                if (item.IsChatPinned && !existing.IsChatPinned && existing.PinnedTimestamp != 0)
                {
                    existing.IsChatPinned = true;
                    existing.PinnedTimestamp = item.PinnedTimestamp;
                }
                else if (item.IsChatPinned && existing.IsChatPinned &&
                         (item.PinnedTimestamp ?? 0) > (existing.PinnedTimestamp ?? 0))
                {
                    existing.PinnedTimestamp = item.PinnedTimestamp;
                }

                DateTime existingPreviewUtc = existing.LastMessageTimestampUtc ?? DateTime.MinValue;
                DateTime itemPreviewUtc = item.LastMessageTimestampUtc ?? DateTime.MinValue;
                bool itemHasNewerPreview = itemPreviewUtc > existingPreviewUtc;
                bool samePreviewButBetterAvatar = itemPreviewUtc == existingPreviewUtc &&
                    string.IsNullOrWhiteSpace(existing.GetAvatarUrl(preferHigh: false)) &&
                    !string.IsNullOrWhiteSpace(item.GetAvatarUrl(preferHigh: false));
                bool samePreviewAndAvatarButBetterName = itemPreviewUtc == existingPreviewUtc &&
                    string.Equals(
                        existing.GetAvatarUrl(preferHigh: false),
                        item.GetAvatarUrl(preferHigh: false),
                        StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(existing.Name) &&
                    !string.IsNullOrWhiteSpace(item.Name);

                if (itemHasNewerPreview || samePreviewButBetterAvatar || samePreviewAndAvatarButBetterName)
                {
                    if (existing.IsChatPinned && !item.IsChatPinned && item.PinnedTimestamp != 0)
                    {
                        item.IsChatPinned = true;
                        item.PinnedTimestamp = existing.PinnedTimestamp;
                    }

                    deduped[existingIndex] = item;
                }
            }

            return deduped;
        }

        public static List<ChatItem> SortForDisplay(IEnumerable<ChatItem> source)
        {
            if (source == null)
            {
                return new List<ChatItem>();
            }

            return source
                .OrderByDescending(c => c.IsChatPinned)
                .ThenByDescending(c => c.PinnedTimestamp ?? 0)
                .ThenByDescending(c => c.LastMessageTimestampUtc ?? DateTime.MinValue)
                .ThenBy(c => c.Name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }
}
