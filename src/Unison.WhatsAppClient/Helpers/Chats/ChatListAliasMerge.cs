using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.Core.Helpers;

namespace Unison.WhatsAppClient.Helpers
{
    /// <summary>
    /// Collapses a LID row and a phone row of the same person once the pair is known, or after
    /// a usync answer that has not filed the pair yet. Also folds a transient direct address
    /// into its canonical row (live self-chat collapse).
    /// </summary>
    /// <remarks>
    /// When the addresses already resolve to one canonical id, the ordinary list dedupe is enough.
    /// When they do not, the timelines are merged under the phone address and the LID row leaves
    /// the list — filing the alias is the caller's job. Persistence after a transient collapse
    /// stays with the caller.
    /// </remarks>
    public sealed class ChatListAliasMerge
    {
        private readonly IDispatcher _ui;
        private readonly ChatStateStore _chats;
        private readonly ChatRowIndex _rows;
        private readonly JidAliasTable _aliases;
        private readonly ChatListDeduper _deduper;
        private readonly ISelfMarkerNaming _selfMarkers;

        public ChatListAliasMerge(
            IDispatcher ui,
            ChatStateStore chats,
            ChatRowIndex rows,
            JidAliasTable aliases,
            ChatListDeduper deduper,
            ISelfMarkerNaming selfMarkers)
        {
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _chats = chats ?? throw new ArgumentNullException(nameof(chats));
            _rows = rows ?? throw new ArgumentNullException(nameof(rows));
            _aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
            _deduper = deduper ?? throw new ArgumentNullException(nameof(deduper));
            _selfMarkers = selfMarkers ?? throw new ArgumentNullException(nameof(selfMarkers));
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

        /// <summary>
        /// Folds a transient direct chat into its canonical address: timeline, list row, name maps.
        /// </summary>
        public async Task<ChatListTransientCollapseResult> CollapseTransientAsync(
            string transientJid,
            string canonicalJid)
        {
            string normalizedTransient = JidHelper.Normalize(transientJid);
            string normalizedCanonical = JidHelper.Normalize(canonicalJid);
            if (string.IsNullOrWhiteSpace(normalizedTransient) ||
                string.IsNullOrWhiteSpace(normalizedCanonical) ||
                string.Equals(normalizedTransient, normalizedCanonical, StringComparison.OrdinalIgnoreCase))
            {
                return ChatListTransientCollapseResult.None;
            }

            bool merged = false;
            List<ChatMessage> canonicalSnapshot = null;

            await _ui.RunAsync(() =>
            {
                if (_chats.MessagesByChat.MergeInto(normalizedTransient, normalizedCanonical))
                {
                    merged = true;
                    canonicalSnapshot = _chats.MessagesByChat[normalizedCanonical].ToList();
                }

                ChatItem transientChat = _chats.Chats.FirstOrDefault(
                    c => c != null &&
                         string.Equals(JidHelper.Normalize(c.JID), normalizedTransient, StringComparison.OrdinalIgnoreCase));
                ChatItem canonicalChat = _chats.Chats.FirstOrDefault(
                    c => c != null &&
                         string.Equals(JidHelper.Normalize(c.JID), normalizedCanonical, StringComparison.OrdinalIgnoreCase));

                if (transientChat != null)
                {
                    if (canonicalChat == null)
                    {
                        transientChat.JID = normalizedCanonical;
                        _rows.Invalidate();
                        canonicalChat = transientChat;
                    }
                    else
                    {
                        TransientChatMerge.Apply(
                            canonicalChat,
                            transientChat,
                            normalizedCanonical,
                            normalizedTransient,
                            _selfMarkers);
                        _chats.Chats.Remove(transientChat);
                    }

                    merged = true;
                }

                if (_chats.PushNames.TryGetValue(normalizedTransient, out string transientName))
                {
                    _chats.PushNames.TryAdd(normalizedCanonical, transientName);
                    _chats.PushNames.Remove(normalizedTransient);
                    merged = true;
                }

                if (_chats.AddressBookNames.TryGetValue(normalizedTransient, out string transientPhoneName))
                {
                    _chats.AddressBookNames.TryAdd(normalizedCanonical, transientPhoneName);
                    _chats.AddressBookNames.Remove(normalizedTransient);
                    merged = true;
                }
            }).ConfigureAwait(false);

            if (!merged)
            {
                return ChatListTransientCollapseResult.None;
            }

            return new ChatListTransientCollapseResult(
                normalizedTransient,
                normalizedCanonical,
                canonicalSnapshot ?? new List<ChatMessage>());
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

    public sealed class ChatListTransientCollapseResult
    {
        public static readonly ChatListTransientCollapseResult None =
            new ChatListTransientCollapseResult(null, null, null);

        public ChatListTransientCollapseResult(
            string transientJid,
            string canonicalJid,
            IReadOnlyList<ChatMessage> canonicalMessages)
        {
            TransientJid = transientJid;
            CanonicalJid = canonicalJid;
            CanonicalMessages = canonicalMessages ?? new List<ChatMessage>();
        }

        public bool Merged => !string.IsNullOrEmpty(TransientJid);

        public string TransientJid { get; }

        public string CanonicalJid { get; }

        public IReadOnlyList<ChatMessage> CanonicalMessages { get; }
    }
}
