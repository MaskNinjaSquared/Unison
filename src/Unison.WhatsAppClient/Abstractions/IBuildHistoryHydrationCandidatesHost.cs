// =============================================================================
// IBuildHistoryHydrationCandidatesHost
//
// Chat rows / counts for BuildHistoryHydrationCandidatesDispatch.
// =============================================================================
using System;

namespace Unison.WhatsAppClient.Abstractions
{
    public sealed class HistoryHydrationChatRow
    {
        public string Jid { get; set; }

        public DateTime? LastMessageTimestampUtc { get; set; }
    }

    public interface IBuildHistoryHydrationCandidatesHost
    {
        int GetHydrationChatCount();

        HistoryHydrationChatRow GetHydrationChatAt(int index);

        string NormalizeHydrationJid(string jid);

        int GetHydrationMessageCount(string jid);

        TimeSpan HistoryBackfillStaleThreshold { get; }

        int HistoryBackfillMaxStaleTopChats { get; }
    }
}
