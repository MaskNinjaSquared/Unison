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
    }
}
