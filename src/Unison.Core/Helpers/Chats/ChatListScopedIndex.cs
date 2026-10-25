// =============================================================================
// ChatListScopedIndex
//
// Index of one row among the chats that pass a scope predicate — used to mirror
// a source Move onto VisibleChats without re-sorting the whole catalogue.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers.Chats
{
    public static class ChatListScopedIndex
    {
        /// <summary>
        /// Zero-based index of <paramref name="chat"/> among scoped rows, or -1 when absent.
        /// Source order is authoritative after <c>ChatDisplayOrder.Reposition</c>.
        /// </summary>
        public static int IndexOf(
            IEnumerable<ChatItem> source,
            ChatItem chat,
            Func<ChatItem, bool> matchesScope)
        {
            if (source == null || chat == null || matchesScope == null)
            {
                return -1;
            }

            int index = 0;
            foreach (ChatItem candidate in source)
            {
                if (candidate == null || !matchesScope(candidate))
                {
                    continue;
                }

                if (ReferenceEquals(candidate, chat))
                {
                    return index;
                }

                index++;
            }

            return -1;
        }
    }
}
