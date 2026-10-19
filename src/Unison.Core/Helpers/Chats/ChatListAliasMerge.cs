using System;
using System.Linq;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Collapses a LID row and a phone row of the same person once the pair is known, or after
    /// a usync answer that has not filed the pair yet.
    /// </summary>
    /// <remarks>
    /// When the addresses already resolve to one canonical id, the ordinary list dedupe is enough.
    /// When they do not, the timelines are merged under the phone address and the LID row leaves
    /// the list — filing the alias is the caller's job.
    /// </remarks>
    public sealed class ChatListAliasMerge
    {
        private readonly IDispatcher _ui;
        private readonly ChatStateStore _chats;
        private readonly JidAliasTable _aliases;
        private readonly ChatListDeduper _deduper;

        public ChatListAliasMerge(
            IDispatcher ui,
            ChatStateStore chats,
            JidAliasTable aliases,
            ChatListDeduper deduper)
        {
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _chats = chats ?? throw new ArgumentNullException(nameof(chats));
            _aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
            _deduper = deduper ?? throw new ArgumentNullException(nameof(deduper));
        }

        public async Task<ChatListAliasMergeResult> MergeAsync(string lidJid, string pnJid)
        {
            string lid = JidHelper.Normalize(lidJid);
            string pn = JidHelper.Normalize(pnJid);
            if (string.IsNullOrEmpty(lid) || string.IsNullOrEmpty(pn))
            {
                return new ChatListAliasMergeResult(ChatListAliasMergeOutcome.None);
            }

            if (string.Equals(
                    _aliases.GetCanonicalJid(lid),
                    _aliases.GetCanonicalJid(pn),
                    StringComparison.OrdinalIgnoreCase))
            {
                await _deduper.RunAsync("alias-merge").ConfigureAwait(false);
                return new ChatListAliasMergeResult(ChatListAliasMergeOutcome.Deduped);
            }

            ChatListAliasMergeOutcome outcome = ChatListAliasMergeOutcome.None;
            await _ui.RunAsync(() =>
            {
                ChatItem lidChat = _chats.Chats.FirstOrDefault(
                    c => c != null && string.Equals(JidHelper.Normalize(c.JID), lid, StringComparison.Ordinal));
                ChatItem pnChat = _chats.Chats.FirstOrDefault(
                    c => c != null && string.Equals(JidHelper.Normalize(c.JID), pn, StringComparison.Ordinal));

                if (lidChat == null || pnChat == null || ReferenceEquals(lidChat, pnChat))
                {
                    return;
                }

                _chats.MessagesByChat.MergeInto(lid, pn);
                _chats.Chats.Remove(lidChat);
                outcome = ChatListAliasMergeOutcome.MergedRows;
            }).ConfigureAwait(false);

            return new ChatListAliasMergeResult(outcome);
        }
    }

    public enum ChatListAliasMergeOutcome
    {
        None,
        Deduped,
        MergedRows
    }

    public sealed class ChatListAliasMergeResult
    {
        public ChatListAliasMergeResult(ChatListAliasMergeOutcome outcome)
        {
            Outcome = outcome;
        }

        public ChatListAliasMergeOutcome Outcome { get; }
    }
}
