// =============================================================================
// ListPreviewOutgoingSendState
//
// Paints the outgoing send ticks onto every list row whose tip is still that
// message (id match, or the short clock fallback for older schema rows).
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Helpers
{
    public static class ListPreviewOutgoingSendState
    {
        /// <summary>
        /// How far a tip without a message id may lag the bubble and still count as the same send.
        /// </summary>
        public static readonly TimeSpan ClockFallbackWindow = TimeSpan.FromSeconds(2);

        public static void Apply(
            IList<ChatItem> rows,
            ChatMessage message,
            string status)
        {
            if (rows == null || message == null || !message.IsFromMe)
            {
                return;
            }

            for (int i = 0; i < rows.Count; i++)
            {
                ChatItem chat = rows[i];
                if (chat == null ||
                    !ChatPreviewTip.ShowsOutgoingMessage(chat, message, ClockFallbackWindow))
                {
                    continue;
                }

                chat.LastMessageSendState = HistoryLiveMessageMapper.FromStatus(status, true);
            }
        }
    }
}
