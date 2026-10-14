// =============================================================================
// NewestStoredMessage
//
// The newest message time the app holds, for history freshness. Timelines are
// mostly empty on a cold start until a chat opens; the list strip already carries
// the tip times read from SQLite, so both count. Every value is compared through
// ChatMessageOrder.ToComparableUtc: live rows are Utc, SQLite rows Unspecified.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class NewestStoredMessage
    {
        /// <param name="includeChat">Sees the normalized chat JID; null counts every chat.</param>
        /// <returns><see cref="DateTime.MinValue"/> when nothing is held.</returns>
        public static DateTime FindUtc(
            IEnumerable<KeyValuePair<string, List<ChatMessage>>> timelines,
            IList<ChatItem> chats,
            Func<string, bool> includeChat)
        {
            DateTime newest = DateTime.MinValue;

            if (timelines != null)
            {
                foreach (KeyValuePair<string, List<ChatMessage>> timeline in timelines)
                {
                    if (!Includes(includeChat, timeline.Key))
                    {
                        continue;
                    }

                    newest = Later(newest, ChatMessageOrder.NewestComparableUtc(timeline.Value));
                }
            }

            if (chats != null)
            {
                for (int i = 0; i < chats.Count; i++)
                {
                    ChatItem chat = chats[i];
                    if (chat?.LastMessageTimestampUtc == null || !Includes(includeChat, chat.JID))
                    {
                        continue;
                    }

                    newest = Later(newest, ChatMessageOrder.ToComparableUtc(chat.LastMessageTimestampUtc.Value));
                }
            }

            return newest;
        }

        private static bool Includes(Func<string, bool> includeChat, string jid) =>
            includeChat == null || includeChat(JidHelper.Normalize(jid));

        private static DateTime Later(DateTime left, DateTime right) => right > left ? right : left;
    }
}
