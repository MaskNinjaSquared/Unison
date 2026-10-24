// =============================================================================
// SharePickerCatalog
//
// Ordered chat rows for the Share Target "Send to…" overlay. Pure so cold-share
// rebuild rules can be unit-tested without ShellViewModel DI.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Unison.Core.Models;

namespace Unison.Core.Helpers.Chats
{
    public static class SharePickerCatalog
    {
        /// <summary>
        /// Snapshot of non-null chats sorted by display name (culture-aware).
        /// Empty input → empty list (never null).
        /// </summary>
        public static IReadOnlyList<ChatItem> BuildOrdered(
            IEnumerable<ChatItem> chats,
            Func<ChatItem, string> resolveName)
        {
            if (chats == null)
            {
                return Array.Empty<ChatItem>();
            }

            Func<ChatItem, string> nameOf = resolveName ?? (c => c?.Name ?? c?.JID ?? string.Empty);

            return chats
                .Where(c => c != null)
                .OrderBy(
                    c =>
                    {
                        string name = nameOf(c);
                        return string.IsNullOrWhiteSpace(name)
                            ? (c.JID ?? string.Empty)
                            : name;
                    },
                    StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Cold share often opens the picker before persisted chats land. While the
        /// overlay stays open, any catalog mutation must rebuild candidates.
        /// </summary>
        public static bool ShouldRebuildWhileOpen(bool isSharePickerOpen, bool catalogChanged)
        {
            return isSharePickerOpen && catalogChanged;
        }
    }
}
