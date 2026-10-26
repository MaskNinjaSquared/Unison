// =============================================================================
// HistoryBackfillSelection
//
// Picks in-memory list chats whose tip looks stale for HISTORY_SYNC_ON_DEMAND.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Helpers;

namespace Unison.WhatsAppClient.Helpers
{
    public static class HistoryBackfillSelection
    {
        public static IReadOnlyList<HistoryBackfillCandidate> SelectTopStale(
            IReadOnlyList<HistoryBackfillChatSnapshot> chatsInDisplayOrder,
            DateTime nowUtc,
            TimeSpan staleThreshold,
            int maxCount)
        {
            var selected = new List<HistoryBackfillCandidate>();
            if (chatsInDisplayOrder == null)
            {
                return selected;
            }

            bool unlimited = maxCount <= 0;
            for (int i = 0; i < chatsInDisplayOrder.Count; i++)
            {
                if (!unlimited && selected.Count >= maxCount)
                {
                    break;
                }

                HistoryBackfillChatSnapshot chat = chatsInDisplayOrder[i];
                if (chat == null || string.IsNullOrWhiteSpace(chat.Jid))
                {
                    continue;
                }

                string reason;
                if (!IsTipStale(chat.LastMessageUtc, nowUtc, staleThreshold, out reason))
                {
                    continue;
                }

                selected.Add(new HistoryBackfillCandidate
                {
                    Jid = chat.Jid,
                    IsGroup = chat.IsGroup,
                    ListIndex = chat.ListIndex >= 0 ? chat.ListIndex : i,
                    MessageCount = chat.MessageCount,
                    IsStale = true,
                    LastMessageTimestamp = chat.LastMessageUtc,
                    SelectionReason = reason
                });
            }

            return selected;
        }

        private static bool IsTipStale(
            DateTime lastMessageUtc,
            DateTime nowUtc,
            TimeSpan staleThreshold,
            out string reason)
        {
            if (lastMessageUtc == DateTime.MinValue)
            {
                reason = "no-tip";
                return true;
            }

            TimeSpan age = nowUtc - lastMessageUtc;
            if (age > staleThreshold)
            {
                reason = "tip-stale:ageHours=" + age.TotalHours.ToString("F1");
                return true;
            }

            reason = null;
            return false;
        }
    }
}
