// =============================================================================
// AliasLinkedMessageFind
//
// Whether this message id is already on screen under one of the contact's
// other addresses. The first chat in the list that holds it wins.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Unison.Core.Models;

namespace Unison.Core.State
{
    public static class AliasLinkedMessageFind
    {
        public static bool TryFind(
            ChatMessageCache messages,
            IEnumerable<string> chatJids,
            string messageId,
            out string existingChatJid,
            out ChatMessage existingMessage)
        {
            existingChatJid = null;
            existingMessage = null;
            if (messages == null || string.IsNullOrWhiteSpace(messageId))
            {
                return false;
            }

            foreach (string chatJid in chatJids ?? Enumerable.Empty<string>())
            {
                List<ChatMessage> rows;
                if (!messages.TryGet(chatJid, out rows) || rows == null)
                {
                    continue;
                }

                ChatMessage match = rows.FirstOrDefault(
                    message => string.Equals(message?.Id, messageId, StringComparison.Ordinal));
                if (match == null)
                {
                    continue;
                }

                existingChatJid = chatJid;
                existingMessage = match;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Fast path: whether any alias bucket already indexes <paramref name="messageId"/>.
        /// Prefer this over <see cref="TryFind"/> when the caller only needs a yes/no.
        /// </summary>
        public static bool HasId(
            ChatMessageCache messages,
            IEnumerable<string> chatJids,
            string messageId)
        {
            if (messages == null || string.IsNullOrWhiteSpace(messageId))
            {
                return false;
            }

            foreach (string chatJid in chatJids ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(chatJid))
                {
                    continue;
                }

                if (messages.KnownIds(chatJid).Contains(messageId))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
