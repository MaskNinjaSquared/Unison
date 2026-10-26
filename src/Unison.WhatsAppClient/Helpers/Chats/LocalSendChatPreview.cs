// =============================================================================
// LocalSendChatPreview
//
// After a local send, every alias row of the conversation must show the new tip
// (and a missing row is created). UI hop / dedupe / persist stay on the host.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Helpers
{
    public static class LocalSendChatPreview
    {
        public static void ApplyToAliasRows(
            ObservableCollection<ChatItem> chats,
            IList<ChatItem> matchingRows,
            string canonicalJid,
            Func<string> resolveName,
            Func<ChatKind> resolveKind,
            string preview,
            DateTime timestamp,
            ChatPreviewKind? kindHint,
            IList<string> mentionedJids,
            string messageId,
            MessageSendState sendState,
            string yesterdayLabel)
        {
            if (chats == null || matchingRows == null || string.IsNullOrWhiteSpace(canonicalJid))
            {
                return;
            }

            if (matchingRows.Count == 0)
            {
                ChatItem created = new ChatItem
                {
                    JID = canonicalJid,
                    Name = resolveName != null ? resolveName() : canonicalJid,
                    Kind = resolveKind != null ? resolveKind() : ChatKind.Direct
                };
                chats.Insert(0, created);
                matchingRows.Add(created);
            }

            for (int i = 0; i < matchingRows.Count; i++)
            {
                ChatItem row = matchingRows[i];
                if (row == null)
                {
                    continue;
                }

                LiveChatPreviewApplier.ApplyIfNewer(
                    row,
                    preview,
                    timestamp,
                    force: true,
                    kindHint,
                    authorPrefix: null,
                    mentionedJids,
                    isFromMe: true,
                    sendState,
                    messageId,
                    yesterdayLabel,
                    participantJid: null,
                    senderName: null);
            }

            IReadOnlyList<ChatItem> tipRows = matchingRows as IReadOnlyList<ChatItem>
                ?? new List<ChatItem>(matchingRows);
            ChatDisplayOrder.Reposition(
                chats,
                DuplicateChatRowPick.PreferredForPreview(tipRows));
        }
    }
}
