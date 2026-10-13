// =============================================================================
// AliasDuplicateMessageMove
//
// One message showed up under both addresses of the same person. It belongs on
// the target chat. If that chat already has the id, the extra copy is dropped
// rather than shown twice.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.State
{
    public static class AliasDuplicateMessageMove
    {
        public static bool TryMove(
            ChatMessageCache messages,
            MessageIdIndex index,
            string targetJid,
            string sourceJid,
            string messageId,
            out ChatMessage moved)
        {
            moved = null;
            if (messages == null ||
                index == null ||
                string.IsNullOrWhiteSpace(targetJid) ||
                string.IsNullOrWhiteSpace(sourceJid) ||
                string.IsNullOrWhiteSpace(messageId) ||
                string.Equals(targetJid, sourceJid, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            List<ChatMessage> sourceMessages;
            if (!messages.TryGet(sourceJid, out sourceMessages) || sourceMessages == null)
            {
                return false;
            }

            ChatMessage existing = sourceMessages.FirstOrDefault(
                message => string.Equals(message?.Id, messageId, StringComparison.Ordinal));
            if (existing == null)
            {
                return false;
            }

            moved = existing;
            List<ChatMessage> targetMessages = messages.GetOrAdd(targetJid);

            if (!index.GetOrBuild(targetJid, () => targetMessages).Contains(messageId))
            {
                ChatMessageOrder.InsertSorted(targetMessages, existing);
                index.Register(targetJid, messageId);
            }

            sourceMessages.Remove(existing);
            index.Remove(sourceJid, messageId);
            return true;
        }
    }
}
