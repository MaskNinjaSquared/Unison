// =============================================================================
// AppStateDeleteMessagePreview
//
// After an account-side message delete, every alias row of the conversation
// must show the new tip (or clear). Pure strip update; the host owns eviction
// and SQLite.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Helpers
{
    public static class AppStateDeleteMessagePreview
    {
        public static void RefreshAliasRows(
            IEnumerable<ChatItem> rows,
            IList<ChatMessage> remainingMessages,
            string canonicalJid,
            Func<string, string> getCanonicalParticipant,
            string selfFallbackName,
            string yesterdayLabel)
        {
            if (rows == null)
            {
                return;
            }

            IReadOnlyList<ChatMessage> tipSource =
                remainingMessages as IReadOnlyList<ChatMessage>
                ?? (remainingMessages == null
                    ? null
                    : (IReadOnlyList<ChatMessage>)remainingMessages.ToList());

            bool isGroupJid = JidHelper.IsGroupJid(canonicalJid);
            foreach (ChatItem chat in rows)
            {
                if (chat == null)
                {
                    continue;
                }

                ChatMessage latest = ChatPreviewTip.PickLatest(tipSource);
                if (latest != null)
                {
                    bool isGroup = isGroupJid || chat.IsGroup;
                    ChatMessageTipApply.Apply(
                        chat,
                        latest,
                        isGroup,
                        force: true,
                        selfFallbackName,
                        getCanonicalParticipant != null
                            ? getCanonicalParticipant(latest.ParticipantJid)
                            : null,
                        yesterdayLabel);
                }
                else
                {
                    ChatPreviewTip.Clear(chat);
                }
            }
        }
    }
}
