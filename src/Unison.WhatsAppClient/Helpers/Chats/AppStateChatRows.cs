// =============================================================================
// AppStateChatRows
//
// Find-or-add the chat rows an app-state mutation lands on. One address can be
// listed under both PN and LID; when the list has none yet, the account still
// spoke about a conversation and a placeholder row is created.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Helpers
{
    public static class AppStateChatRows
    {
        /// <summary>
        /// Every row for <paramref name="canonical"/>, adding one when the list is empty.
        /// Caller must already be on the UI thread that owns <paramref name="chats"/>.
        /// </summary>
        public static List<ChatItem> GetOrAdd(
            IList<ChatItem> chats,
            IReadOnlyList<ChatItem> existingRows,
            string canonical,
            Func<string, string> resolveDisplayName,
            Func<string, ChatKind> resolveKind,
            out ChatItem created)
        {
            created = null;
            var rows = existingRows != null
                ? new List<ChatItem>(existingRows)
                : new List<ChatItem>();
            if (rows.Count > 0)
            {
                return rows;
            }

            if (chats == null || string.IsNullOrWhiteSpace(canonical))
            {
                return rows;
            }

            if (resolveDisplayName == null)
            {
                throw new ArgumentNullException(nameof(resolveDisplayName));
            }

            if (resolveKind == null)
            {
                throw new ArgumentNullException(nameof(resolveKind));
            }

            created = new ChatItem
            {
                JID = canonical,
                Name = resolveDisplayName(canonical),
                Kind = resolveKind(canonical)
            };
            chats.Add(created);
            rows.Add(created);
            return rows;
        }
    }
}
