// =============================================================================
// HistoryHydrationCandidatesOwner
//
// Owns IBuildHistoryHydrationCandidatesHost: picks the stale top chats for
// per-JID ON_DEMAND hydration from the live chat list and stored counts.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Models;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class HistoryHydrationCandidatesOwner : IBuildHistoryHydrationCandidatesHost
    {
        private readonly Func<IReadOnlyList<ChatItem>> _chats;
        private readonly Func<string, string> _normalizeJid;
        private readonly Func<string, int> _getMessageCount;
        private readonly TimeSpan _staleThreshold;
        private readonly int _maxStaleTopChats;

        public HistoryHydrationCandidatesOwner(
            Func<IReadOnlyList<ChatItem>> chats,
            Func<string, string> normalizeJid,
            Func<string, int> getMessageCount,
            TimeSpan staleThreshold,
            int maxStaleTopChats)
        {
            _chats = chats ?? throw new ArgumentNullException(nameof(chats));
            _normalizeJid = normalizeJid ?? throw new ArgumentNullException(nameof(normalizeJid));
            _getMessageCount = getMessageCount ?? throw new ArgumentNullException(nameof(getMessageCount));
            _staleThreshold = staleThreshold;
            _maxStaleTopChats = maxStaleTopChats;
        }

        public IReadOnlyList<HistoryBackfillCandidate> Build(DateTime nowUtc) =>
            BuildHistoryHydrationCandidatesDispatch.Run(this, nowUtc);

        int IBuildHistoryHydrationCandidatesHost.GetHydrationChatCount() => _chats().Count;

        HistoryHydrationChatRow IBuildHistoryHydrationCandidatesHost.GetHydrationChatAt(int index)
        {
            ChatItem chat = _chats()[index];
            if (chat == null)
            {
                return null;
            }

            return new HistoryHydrationChatRow
            {
                Jid = chat.JID,
                LastMessageTimestampUtc = chat.LastMessageTimestampUtc
            };
        }

        string IBuildHistoryHydrationCandidatesHost.NormalizeHydrationJid(string jid) => _normalizeJid(jid);

        int IBuildHistoryHydrationCandidatesHost.GetHydrationMessageCount(string jid) => _getMessageCount(jid);

        TimeSpan IBuildHistoryHydrationCandidatesHost.HistoryBackfillStaleThreshold => _staleThreshold;

        int IBuildHistoryHydrationCandidatesHost.HistoryBackfillMaxStaleTopChats => _maxStaleTopChats;
    }
}
