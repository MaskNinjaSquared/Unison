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
        private readonly IWhatsAppService _whatsApp;
        private readonly IStringResources _strings;
        private readonly IDispatcher _dispatcher;

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
            IWhatsAppService whatsApp,
            IStringResources strings,
            IDispatcher dispatcher)
        {
            _chatState = chatState ?? throw new ArgumentNullException(nameof(chatState));
            _personStore = personStore ?? throw new ArgumentNullException(nameof(personStore));
            _whatsApp = whatsApp;
            _strings = strings;
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
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
            _chatState.Chats.CollectionChanged += OnChatsChanged;

            ScheduleSweep();
        }

        private void OnPersonChanged(object sender, string jid) => ScheduleSweep();

        private void OnDisplayNamesChanged(object sender, EventArgs e) => ScheduleSweep();

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

                string resolved = ResolveParticipantDisplayName(participant);
                if (string.IsNullOrWhiteSpace(resolved))
                {
                    // Nothing in memory. The name may still be on disk from an earlier session.
                    if (MarkForWarmUp(participant))
                    {
                        (needsStoreLookup ?? (needsStoreLookup = new List<string>())).Add(participant);
                    }

                    continue;
                }

                if (string.Equals(resolved, chat.LastMessageSenderName, StringComparison.Ordinal))
                {
                    continue;
                }

                string prefix = ChatPreviewNormalizer.FormatListAuthorPrefix(
                    new ChatMessage
                    {
                        IsFromMe = false,
                        SenderName = resolved,
                        ParticipantJid = participant
                    },
                    true,
                    selfLabel);

                if (!string.IsNullOrEmpty(prefix))
                {
                    chat.LastMessageSenderName = resolved;
                    chat.LastMessageAuthor = prefix;
                }
            }

            if (needsStoreLookup != null)
            {
                _ = WarmFromStoreAsync(needsStoreLookup);
            }
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

                    string canonical = _whatsApp?.GetCanonicalJid(jid);
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
        /// Best label for a participant across the JID's own and canonical (LID → PN) forms.
        /// Null when nothing better than the raw JID is known yet.
        /// </summary>
        private string ResolveParticipantDisplayName(string participantJid)
        {
            string name = ResolveNameForJid(participantJid);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            string canonical = _whatsApp?.GetCanonicalJid(participantJid);
            if (!string.IsNullOrWhiteSpace(canonical) &&
                !string.Equals(canonical, participantJid, StringComparison.OrdinalIgnoreCase))
            {
                name = ResolveNameForJid(canonical);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }

            return null;
        }

        /// <summary>
        /// One JID, every in-memory source: resolved-name map, the Person cache the roster writes
        /// into, then the 1:1 chat with that person. No disk I/O — runs per list row.
        /// </summary>
        private string ResolveNameForJid(string jid)
        {
            string name = _chatState.ResolveDisplayName(jid);
            if (IsUsableParticipantLabel(name, jid))
            {
                return name;
            }

            Person person = _personStore?.TryGetCached(jid);
            if (person != null && IsUsableParticipantLabel(person.Name, jid))
            {
                return person.Name;
            }

            ChatItem direct = _chatState.FindChat(jid);
            if (direct != null && IsUsableParticipantLabel(direct.Name, jid))
            {
                return direct.Name;
            }

            return null;
        }

        /// <summary>
        /// A label equal to the JID's own digits is not a name — using it would just re-print the
        /// LID we are trying to replace.
        /// </summary>
        private static bool IsUsableParticipantLabel(string candidate, string jid)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            string bare = (jid ?? string.Empty).Split('@')[0].Split(':')[0];
            return !string.Equals(candidate.Trim(), bare, StringComparison.OrdinalIgnoreCase);
        }
    }
}
