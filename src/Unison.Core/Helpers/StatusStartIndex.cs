using System;
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>Viewer start position for a tapped Status item (0 when unknown).</summary>
    public static class StatusStartIndex
    {
        public static int Resolve(IReadOnlyList<HistoryStatus> items, string messageId)
        {
            return Find(items, messageId) ?? 0;
        }

        /// <summary>Index of <paramref name="messageId"/> in <paramref name="items"/>; null when absent.</summary>
        public static int? Find(IReadOnlyList<HistoryStatus> items, string messageId)
        {
            if (items == null || string.IsNullOrWhiteSpace(messageId))
            {
                return null;
            }

            for (int i = 0; i < items.Count; i++)
            {
                if (string.Equals(items[i]?.MessageId, messageId, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return null;
        }
    }
}
