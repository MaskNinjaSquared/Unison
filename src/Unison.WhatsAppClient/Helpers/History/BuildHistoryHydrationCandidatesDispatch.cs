// =============================================================================
// BuildHistoryHydrationCandidatesDispatch
//
// Body of BuildHistoryHydrationCandidates. Chat list / counts stay on the host.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class BuildHistoryHydrationCandidatesDispatch
    {
        public static IReadOnlyList<HistoryBackfillCandidate> Run(
            IBuildHistoryHydrationCandidatesHost host,
            DateTime nowUtc)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            int count = host.GetHydrationChatCount();
            var snapshots = new List<HistoryBackfillChatSnapshot>(count);
            for (int i = 0; i < count; i++)
            {
                HistoryHydrationChatRow chat = host.GetHydrationChatAt(i);
                if (chat == null || string.IsNullOrWhiteSpace(chat.Jid))
                {
                    continue;
                }

                string jid = host.NormalizeHydrationJid(chat.Jid);
                int messageCount = host.GetHydrationMessageCount(jid);
                if (messageCount <= 0)
                {
                    continue;
                }

                DateTime tipUtc = chat.LastMessageTimestampUtc ?? DateTime.MinValue;
                if (tipUtc != DateTime.MinValue && tipUtc.Kind != DateTimeKind.Utc)
                {
                    tipUtc = tipUtc.ToUniversalTime();
                }

                snapshots.Add(new HistoryBackfillChatSnapshot
                {
                    Jid = jid,
                    IsGroup = JidHelper.IsGroupJid(jid),
                    ListIndex = i,
                    LastMessageUtc = tipUtc,
                    MessageCount = messageCount
                });
            }

            return HistoryBackfillSelection.SelectTopStale(
                snapshots,
                nowUtc,
                host.HistoryBackfillStaleThreshold,
                host.HistoryBackfillMaxStaleTopChats);
        }
    }
}
