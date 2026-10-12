using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.State
{
    /// <summary>
    /// App-lifetime projection: listens for resolved names and rewrites the group author strip on
    /// the chats that were showing a bare LID/phone. Lives outside <see cref="ChatListViewModel"/>
    /// so the strip catches up even when the list is not the active screen.
    /// </summary>
    public sealed class ChatAuthorProjection : IChatAuthorProjection
    {
        private readonly IChatStateStore _chatState;
        private readonly IPersonStore _personStore;
        private readonly IJidResolver _jids;
        private readonly IStringResources _strings;
        private readonly IDispatcher _dispatcher;
        private readonly IChatService _chats;

        /// <summary>
        /// Same sources, same order the timeline bubbles use. The strip resolving a sender through
        /// a shorter list than the bubble is what let an open group show the name above the message
        /// and the JID in the list at the same time.
        /// </summary>
        private readonly ParticipantResolutionContext _participants;

        private readonly object _gate = new object();

        /// <summary>
        /// JIDs already looked up in SQLite. The Person cache starts cold every launch, so the
        /// first sweep has to pull rows in; this keeps it to one attempt per JID (a participant we
        /// have no row for must not re-query on every sweep).
        /// </summary>
        private readonly HashSet<string> _warmAttempted =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private bool _started;
        private bool _sweepScheduled;

        public ChatAuthorProjection(
            IChatStateStore chatState,
            IPersonStore personStore,
            IJidResolver jids,
            IStringResources strings,
            IDispatcher dispatcher,
            IContactService contacts = null,
            IChatService chats = null)
        {
            _chatState = chatState ?? throw new ArgumentNullException(nameof(chatState));
            _personStore = personStore ?? throw new ArgumentNullException(nameof(personStore));
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
            _strings = strings;
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _chats = chats;
            _participants = new ParticipantResolutionContext(jids, contacts, chatState, personStore);
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_started)
                {
                    return;
                }

                _started = true;
            }

            _personStore.PersonChanged += OnPersonChanged;
            _chatState.DisplayNamesChanged += OnDisplayNamesChanged;
            _chatState.GroupRosterChanged += OnGroupRosterChanged;
            _chatState.Chats.CollectionChanged += OnChatsChanged;

            ScheduleSweep();
        }

        public void Refresh() => ScheduleSweep();

        private void OnPersonChanged(object sender, string jid) => ScheduleSweep();

        private void OnDisplayNamesChanged(object sender, EventArgs e) => ScheduleSweep();

        /// <summary>
        /// One signal per roster apply, not one per member: a group metadata reply merges names onto
        /// every <see cref="GroupMember"/>, and subscribing to those would be N handlers and N
        /// sweeps for one answer. <see cref="ScheduleSweep"/> collapses whatever arrives into one
        /// pass per UI turn.
        /// </summary>
        private void OnGroupRosterChanged(object sender, string groupJid)
        {
            // The roster brings LID↔PN pairs with it, so a store lookup that missed before this
            // group was known can now hit under the phone JID. Only this row's sender is retried;
            // clearing the whole set would re-read SQLite for every group during a sync burst.
            string participant = _chatState.FindChat(groupJid)?.LastMessageParticipantJid;
            if (!string.IsNullOrWhiteSpace(participant))
            {
                lock (_gate)
                {
                    _warmAttempted.Remove(participant);
                }
            }

            ScheduleSweep();
        }

        private void OnChatsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add ||
                e.Action == NotifyCollectionChangedAction.Replace ||
                e.Action == NotifyCollectionChangedAction.Reset)
            {
                ScheduleSweep();
            }
        }

        /// <summary>
        /// Names arrive in bursts during sync. Collapse them into one pass per UI turn instead of
        /// re-walking the list for every merged name.
        /// </summary>
        private void ScheduleSweep()
        {
            lock (_gate)
            {
                if (_sweepScheduled)
                {
                    return;
                }

                _sweepScheduled = true;
            }

            _ = _dispatcher.RunAsync(() =>
            {
                lock (_gate)
                {
                    _sweepScheduled = false;
                }

                Sweep();
            });
        }

        private void Sweep()
        {
            string selfLabel = _strings != null
                ? _strings.Get("Chat_SelfFallbackName", "You")
                : "You";

            List<string> needsStoreLookup = null;
            List<ChatItem> dirtyStrips = null;

            foreach (var chat in _chatState.Chats)
            {
                if (chat == null || !chat.IsGroup || chat.LastMessageIsFromMe)
                {
                    continue;
                }

                string participant = chat.LastMessageParticipantJid;
                if (string.IsNullOrWhiteSpace(participant))
                {
                    continue;
                }

                string beforeAuthor = chat.LastMessageAuthor;
                string beforeSender = chat.LastMessageSenderName;

                // A strip poisoned with the group subject must not count as a real name —
                // otherwise Decide keeps it forever.
                if (!string.IsNullOrWhiteSpace(chat.Name) &&
                    !string.IsNullOrWhiteSpace(chat.LastMessageSenderName) &&
                    string.Equals(
                        chat.LastMessageSenderName.Trim(),
                        chat.Name.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    chat.LastMessageSenderName = null;
                    if (!string.IsNullOrEmpty(chat.LastMessageAuthor) &&
                        chat.LastMessageAuthor.StartsWith(
                            chat.Name.Trim(),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        chat.LastMessageAuthor = string.Empty;
                    }
                }

                string resolved = ResolveParticipantDisplayName(chat, participant);
                if (resolved == null && MarkForWarmUp(participant))
                {
                    // Nothing in memory. The name may still be on disk from an earlier session.
                    (needsStoreLookup ?? (needsStoreLookup = new List<string>())).Add(participant);
                }

                string canonical = _jids.GetCanonicalJid(participant);
                string number = ParticipantStripLabel.ForParticipant(participant, canonical);

                switch (ChatAuthorStripDecision.Decide(
                    chat.LastMessageSenderName,
                    IsUsableParticipantLabel(chat.LastMessageSenderName, participant),
                    resolved,
                    resolved != null,
                    !string.IsNullOrEmpty(number)))
                {
                    case ChatAuthorStripAction.ShowName:
                        ApplyStrip(chat, participant, canonical, resolved, selfLabel);
                        break;

                    case ChatAuthorStripAction.ShowNumber:
                        // The number goes in the strip but not in the sender-name slot: it is a
                        // stand-in, and leaving that slot empty keeps the next sweep looking.
                        ApplyStrip(chat, participant, canonical, null, selfLabel);
                        break;
                }

                if (!string.Equals(beforeAuthor, chat.LastMessageAuthor, StringComparison.Ordinal) ||
                    !string.Equals(beforeSender, chat.LastMessageSenderName, StringComparison.Ordinal))
                {
                    (dirtyStrips ?? (dirtyStrips = new List<ChatItem>())).Add(chat);
                }
            }

            if (dirtyStrips != null && _chats != null)
            {
                // Without this the next cold start reloads an empty author (ComposeAuthor had
                // nothing usable from LID + no sender name) and the strip goes blank again.
                _chats.PersistChatListRows(dirtyStrips);
            }

            if (needsStoreLookup != null)
            {
                _ = WarmFromStoreAsync(needsStoreLookup);
            }
        }

        /// <summary>
        /// Writes the strip for one row. <paramref name="resolvedName"/> null means "no name known":
        /// the prefix falls back to the sender's number and the row's name slot is left empty.
        /// </summary>
        private void ApplyStrip(
            ChatItem chat,
            string participantJid,
            string canonicalJid,
            string resolvedName,
            string selfLabel)
        {
            string prefix = ChatPreviewNormalizer.FormatListAuthorPrefix(
                new ChatMessage
                {
                    IsFromMe = false,
                    SenderName = resolvedName,
                    ParticipantJid = participantJid
                },
                true,
                selfLabel,
                canonicalJid);

            if (string.IsNullOrEmpty(prefix))
            {
                return;
            }

            if (resolvedName != null)
            {
                chat.LastMessageSenderName = resolvedName;
            }

            chat.LastMessageAuthor = prefix;
        }

        private bool MarkForWarmUp(string participantJid)
        {
            lock (_gate)
            {
                return _warmAttempted.Add(participantJid);
            }
        }

        /// <summary>
        /// Pulls participants the in-memory maps could not name out of SQLite (history sync already
        /// wrote their push names there) and re-sweeps if anything came back. Without this the strip
        /// would sit on a bare LID until some unrelated write happened to warm the cache — the
        /// contact-name sidecar only loads in deferred maintenance, which Mobile often skips.
        /// </summary>
        private async Task WarmFromStoreAsync(List<string> participantJids)
        {
            bool loadedAny = false;

            foreach (string jid in participantJids)
            {
                try
                {
                    Person person = await _personStore.GetAsync(jid).ConfigureAwait(false);
                    if (person != null && !string.IsNullOrWhiteSpace(person.Name))
                    {
                        loadedAny = true;
                        continue;
                    }

                    string canonical = _jids?.GetCanonicalJid(jid);
                    if (string.IsNullOrWhiteSpace(canonical) ||
                        string.Equals(canonical, jid, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    person = await _personStore.GetAsync(canonical).ConfigureAwait(false);
                    if (person != null && !string.IsNullOrWhiteSpace(person.Name))
                    {
                        loadedAny = true;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ChatAuthorProjection] Warm-up failed for " + jid + ": " + ex.Message);
                }
            }

            if (loadedAny)
            {
                ScheduleSweep();
            }
        }

        /// <summary>
        /// Best label for the sender, through the same chain the timeline bubbles use: the group
        /// roster, the account's name caches, the Person table, then the 1:1 chat. Null when
        /// nothing better than a bare JID/number is known yet. No disk I/O — runs per list row.
        /// </summary>
        private string ResolveParticipantDisplayName(ChatItem groupChat, string participantJid)
        {
            string resolved = GroupParticipantResolver.ResolveDisplayName(
                participantJid,
                groupChat,
                _participants);

            if (!IsUsableParticipantLabel(resolved, participantJid))
            {
                return null;
            }

            // Never put the group subject in the author strip — that is what the old
            // chatResolvedName fallback did when the envelope omitted participant.
            if (!string.IsNullOrWhiteSpace(groupChat?.Name) &&
                string.Equals(resolved.Trim(), groupChat.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return resolved.Trim();
        }

        /// <summary>
        /// Real display name only — not empty, not a raw JID/user part, not a phone-like digit string.
        /// Phone vs LID bare used to look "usable" and then blocked upgrades to the real name.
        /// </summary>
        private bool IsUsableParticipantLabel(string candidate, string jid)
        {
            if (!GroupParticipantResolver.IsUsableDisplayLabel(candidate, jid))
            {
                return false;
            }

            string canonical = _jids?.GetCanonicalJid(jid);
            if (!string.IsNullOrWhiteSpace(canonical) &&
                !string.Equals(canonical, jid, StringComparison.OrdinalIgnoreCase) &&
                !GroupParticipantResolver.IsUsableDisplayLabel(candidate, canonical))
            {
                return false;
            }

            return true;
        }
    }
}
