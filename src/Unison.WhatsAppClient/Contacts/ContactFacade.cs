// =============================================================================
// ContactFacade
//
// The one implementation of IContactService: who the user knows, under which
// name, and with which picture.
//
// Contacts are not one feature but five, and each of them owns state the others
// must not touch - the session the LID resolver is bound to, the cooldown that
// keeps name resolution from taking over, the sets that stop the same avatar
// being fetched twice, the member-picture batch cursor. Each of those lives
// with the operations that read it, in Contacts/, and this class is what makes
// them look like one feature from outside.
//
// So there is almost nothing here: a delegation per method, and the two places
// where the answer depends on a choice this class is the only one positioned to
// make - which path a number search takes when the socket cannot answer.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.Core.Constants;
using Unison.Socket.Signal;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;

namespace Unison.WhatsAppClient.Contacts
{
    public sealed class ContactFacade : IContactService
    {
        private readonly IPersonStore _personStore;
        private readonly IDispatcher _ui;
        private readonly IChatCatalogPersistence _catalog;
        private readonly IJidResolver _jids;
        private readonly ILocalContactsService _localContacts;
        private readonly ILocalSettings _localSettings;
        private readonly IAppLifecycle _lifecycle;
        private readonly JidAliasTable _aliases;
        private readonly DisplayNameTable _pushNames;
        private readonly DisplayNameTable _addressBookNames;
        private readonly DisplayNameTable _groupSubjects;
        private readonly IChatStateStore _chatState;
        private readonly ContactDisplayNames _displayNames;
        private readonly ChatNameRefresh _nameRefresh;
        private readonly ChatListDeduper _deduper;
        private readonly ChatRowIndex _chatRows;
        private readonly IJidAliasPersistence _aliasPersist;
        private readonly IChatIdentityPersistence _identity;
        private readonly IResolvedNameApplyHost _resolvedNames;
        private readonly ISelfMarkerNaming _selfMarkers;
        private readonly ContactLabelSanitizer _labels;
        private readonly AliasFollowUpQueue _aliasFollowUps = new AliasFollowUpQueue();
        private readonly CoalescedDebounceScheduler _aliasFollowUpDebounce =
            new CoalescedDebounceScheduler(TimeSpan.FromMilliseconds(750));

        private readonly ContactDirectory _directory;
        private readonly AddressBookOverlay _addressBook;
        private readonly ContactUsyncResolver _usync;
        private readonly ContactNameResolver _names;
        private readonly ChatAvatarPolicy _avatars;
        private readonly IAvatarCache _avatarCache;
        private readonly GroupRosterPolicy _roster;
        private readonly IGroupService _groups;
        private readonly SyncStatusChannel _syncStatus;
        private readonly SyncWorkGate _syncWork;
        private readonly IDeferredAvatarResolution _deferredAvatars;
        private readonly BackgroundResolutionPass _backgroundResolution;
        private readonly IRuntimeDiagnostics _diagnostics;
        private DateTime _lastWindowsPublishUtc = DateTime.MinValue;
        private int _lastWindowsPublishCount;
        private int _windowsPublishBusy;

        public ContactFacade(
            IWhatsAppSessionProvider sessions,
            ILocalContactsService localContacts,
            IPersonStore personStore,
            LidMappingStore lidMappings,
            ILocalSettings localSettings,
            IJidResolver jids,
            IUsyncGate usyncGate,
            IGroupService groups,
            IAvatarFetchActions avatarFetch,
            IChatAvatarFetch chatAvatarFetch,
            IChatStateStore chats,
            IAvatarCache avatarCache,
            JidAliasTable aliases,
            DisplayNameTable pushNames,
            DisplayNameTable addressBookNames,
            DisplayNameTable groupSubjects,
            ISelfMarkerNaming selfMarkers,
            IDispatcher ui,
            IChatCatalogPersistence catalog,
            SyncStatusChannel syncStatus,
            SyncWorkGate syncWork,
            ContactDisplayNames displayNames,
            SyncBudget syncBudget,
            SyncWorkloadStrategy workload,
            InitialSyncProgressState initialSync,
            ChatNameRefresh nameRefresh,
            ChatListAliasMerge aliasMerge,
            IDeferredAvatarResolution deferredAvatars,
            IBackgroundResolutionGate backgroundResolutionGate,
            IUsyncAliasHost usyncAliases,
            IAppLifecycle lifecycle,
            ChatListDeduper deduper,
            ChatRowIndex chatRows,
            IJidAliasPersistence aliasPersist,
            IChatIdentityPersistence identity,
            IResolvedNameApplyHost resolvedNames,
            IRuntimeDiagnostics diagnostics = null)
        {
            _displayNames = displayNames ?? throw new ArgumentNullException(nameof(displayNames));
            _nameRefresh = nameRefresh ?? throw new ArgumentNullException(nameof(nameRefresh));
            _deduper = deduper ?? throw new ArgumentNullException(nameof(deduper));
            _chatRows = chatRows ?? throw new ArgumentNullException(nameof(chatRows));
            _aliasPersist = aliasPersist ?? throw new ArgumentNullException(nameof(aliasPersist));
            _identity = identity ?? throw new ArgumentNullException(nameof(identity));
            _resolvedNames = resolvedNames ?? throw new ArgumentNullException(nameof(resolvedNames));
            _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
            _selfMarkers = selfMarkers ?? throw new ArgumentNullException(nameof(selfMarkers));
            _syncStatus = syncStatus ?? throw new ArgumentNullException(nameof(syncStatus));
            _syncWork = syncWork ?? throw new ArgumentNullException(nameof(syncWork));
            _deferredAvatars = deferredAvatars ?? throw new ArgumentNullException(nameof(deferredAvatars));
            _diagnostics = diagnostics;
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            if (sessions == null) throw new ArgumentNullException(nameof(sessions));
            if (localContacts == null) throw new ArgumentNullException(nameof(localContacts));
            if (lidMappings == null) throw new ArgumentNullException(nameof(lidMappings));
            if (usyncGate == null) throw new ArgumentNullException(nameof(usyncGate));
            if (groups == null) throw new ArgumentNullException(nameof(groups));
            if (avatarFetch == null) throw new ArgumentNullException(nameof(avatarFetch));
            if (chatAvatarFetch == null) throw new ArgumentNullException(nameof(chatAvatarFetch));
            _chatState = chats ?? throw new ArgumentNullException(nameof(chats));
            if (avatarCache == null) throw new ArgumentNullException(nameof(avatarCache));

            _personStore = personStore ?? throw new ArgumentNullException(nameof(personStore));
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
            _avatarCache = avatarCache;
            _localContacts = localContacts;
            _localSettings = localSettings ?? throw new ArgumentNullException(nameof(localSettings));
            _aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
            _pushNames = pushNames ?? throw new ArgumentNullException(nameof(pushNames));
            _addressBookNames = addressBookNames ?? throw new ArgumentNullException(nameof(addressBookNames));
            _groupSubjects = groupSubjects ?? throw new ArgumentNullException(nameof(groupSubjects));

            _labels = new ContactLabelSanitizer(selfMarkers);
            _directory = new ContactDirectory(sessions, lidMappings);
            _addressBook = new AddressBookOverlay(localContacts, personStore, jids, ui, addressBookNames, chats);
            _usync = new ContactUsyncResolver(
                usyncAliases ?? throw new ArgumentNullException(nameof(usyncAliases)),
                _directory,
                usyncGate,
                jids,
                pushNames,
                _labels,
                chats,
                avatarFetch,
                ui,
                nameRefresh,
                aliasMerge,
                _syncWork,
                _deferredAvatars,
                diagnostics);
            _names = new ContactNameResolver(
                _addressBook,
                _directory,
                _usync,
                jids,
                new PushNameLookup(pushNames, aliases, _labels),
                groups,
                ui,
                catalog,
                chats,
                _syncStatus,
                _syncWork,
                sessions,
                nameRefresh,
                workload ?? throw new ArgumentNullException(nameof(workload)));
            _avatars = new ChatAvatarPolicy(
                _deferredAvatars,
                jids,
                chatAvatarFetch,
                ui,
                catalog,
                chats,
                _syncStatus,
                _syncWork,
                sessions,
                syncBudget,
                workload ?? throw new ArgumentNullException(nameof(workload)),
                initialSync,
                diagnostics);
            _groups = groups;
            _roster = new GroupRosterPolicy(groups, this, jids, ui, catalog, chats, syncWork, sessions, workload);
            _backgroundResolution = new BackgroundResolutionPass(
                backgroundResolutionGate ?? throw new ArgumentNullException(nameof(backgroundResolutionGate)),
                _deferredAvatars,
                NotifyBackgroundResolutionDueAsync);

            // Lives as long as the app does, so there is nothing to unhook from.
            _nameRefresh.Applied += OnChatNamesApplied;
        }

        private void OnChatNamesApplied(object sender, int updated)
        {
            Debug.WriteLine("[ContactFacade] Applied resolved display names to " + updated + " chats");
            if (_diagnostics != null)
            {
                _diagnostics.Write("names", "list-names-applied", "count=" + updated);
            }

            NotifyDisplayNamesUpdated();
        }

        /// <inheritdoc />
        public void NotifyAvatarCached(string jid, string localAvatarUrl)
        {
            // Not awaited: the write swallows its own failures and the host has nothing to do with
            // the result.
            _ = NotifyAvatarCachedAsync(jid, localAvatarUrl);
        }

        /// <inheritdoc />
        public void NotifyJidAliasResolved(string phoneJid, string lidJid, string source)
        {
            // An earlier lookup may have asked under only the LID or only the PN and stamped a
            // false "no-picture". Now that the pair is known, clear the false miss and coalesce
            // the follow-up (alias file, list fold, avatar retry).
            ClearAvatarAttempted(lidJid);
            ClearAvatarAttempted(phoneJid);
            ClearAvatarAttempted(_jids.GetCanonicalJid(phoneJid));
            _aliasFollowUps.NoteAvatarTarget(phoneJid);
            ScheduleAliasFollowUp(source);
        }

        /// <inheritdoc />
        public void CancelAliasFollowUp() => _aliasFollowUpDebounce.Cancel();

        /// <summary>Group subjects from account app-state / listing into the shared name cache.</summary>
        public Task ApplyRemoteGroupSubjectsAsync(IEnumerable<KeyValuePair<string, string>> subjectsByJid)
        {
            List<ResolvedNameEntry> entries = AppStateResolvedNameEntries.ForGroupSubjects(
                subjectsByJid,
                JidHelper.Normalize,
                _jids.GetCanonicalJid,
                SanitizeForResolvedName);
            return ApplyResolvedNameBatchAsync(entries);
        }

        /// <summary>One contact name from account app-state.</summary>
        public Task ApplyRemoteContactNameAsync(string jid, string name)
        {
            List<ResolvedNameEntry> entries = AppStateResolvedNameEntries.ForContact(
                jid,
                name,
                JidHelper.Normalize,
                _jids.GetCanonicalJid,
                SanitizeForResolvedName);
            return ApplyResolvedNameBatchAsync(entries);
        }

        private string SanitizeForResolvedName(string label, string contextJid)
        {
            ContactLabelResult result = _labels.Sanitize(label, contextJid);
            return result.IsUsable ? result.Label : null;
        }

        /// <summary>Self push-name from account app-state.</summary>
        public async Task ApplyRemoteSelfPushNameAsync(string name)
        {
            CaptureSelfPushName(name, SelfPushNameCapture.AppStateSource);

            string selfJid = _resolvedNames.SelfId;
            string applied = _resolvedNames.AuthSelfName;
            if (string.IsNullOrWhiteSpace(selfJid) || string.IsNullOrWhiteSpace(applied))
            {
                return;
            }

            await ApplyRemoteContactNameAsync(selfJid, applied).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public void CaptureSelfPushName(string pushName, string source = null)
        {
            if (!_resolvedNames.HasSelfCredentials || string.IsNullOrWhiteSpace(pushName))
            {
                return;
            }

            if (SelfPushNameCapture.IsDashPlaceholder(pushName))
            {
                Debug.WriteLine(
                    "[ContactFacade] Ignoring placeholder self push name '-' from " + (source ?? "unknown"));
                return;
            }

            string selfJid = _resolvedNames.SelfId;
            string sanitized = SanitizeForResolvedName(pushName, selfJid);
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                return;
            }

            string established = SelfDisplayNameResolution.Established(
                _resolvedNames.SidebarSelfName,
                _resolvedNames.AuthSelfName,
                _resolvedNames.IsOwnPhoneEchoLabel);
            bool fromAppState = string.Equals(
                source,
                SelfPushNameCapture.AppStateSource,
                StringComparison.Ordinal);
            string preferred = SelfPushNameCapture.PreferEstablished(established, sanitized, fromAppState);
            if (!string.Equals(sanitized, preferred, StringComparison.Ordinal))
            {
                Debug.WriteLine(
                    "[ContactFacade] Keeping established self name '" + established +
                    "'; ignoring '" + sanitized + "' from " + (source ?? "unknown"));
                sanitized = preferred;
            }

            bool authChanged = !string.Equals(_resolvedNames.AuthSelfName, sanitized, StringComparison.Ordinal);
            bool profileChanged = !string.Equals(_resolvedNames.SidebarSelfName, sanitized, StringComparison.Ordinal);
            if (!authChanged && !profileChanged)
            {
                return;
            }

            _resolvedNames.WriteSelfDisplayName(sanitized, persistAuth: authChanged);

            RememberPersonName(selfJid, sanitized);
            RememberPersonName(_resolvedNames.SelfLid, sanitized);

            Debug.WriteLine(
                "[ContactFacade] Self push name captured from " + (source ?? "unknown") +
                ": '" + sanitized + "'");

            _ = ApplyResolvedNamesToChatListAsync();
        }

        /// <inheritdoc />
        public void RememberPersonName(string jid, string displayName)
        {
            if (string.IsNullOrWhiteSpace(jid) || string.IsNullOrWhiteSpace(displayName))
            {
                return;
            }

            string norm = JidHelper.Normalize(jid);
            string sanitized = SanitizeForResolvedName(displayName, norm);
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                return;
            }

            _pushNames.Set(norm, sanitized);

            // History sync repeats the same push name on every message; gate skips no-op writes.
            if (!PersonNamePersistGate.ShouldPersist(_personStore.TryGetCached(norm), sanitized))
            {
                return;
            }

            _ = PersistObservedPersonNameAsync(norm, sanitized);
        }

        /// <inheritdoc />
        public void RegisterIncomingEnvelopeAliasHints(
            string senderLid,
            string fromJid,
            string peerRecipientPn,
            string peerRecipientLid,
            string recipientJid,
            string participant,
            string participantAlt)
        {
            IReadOnlyList<IncomingAliasHint> hints = IncomingEnvelopeAliasHints.Collect(
                senderLid,
                fromJid,
                peerRecipientPn,
                peerRecipientLid,
                recipientJid,
                participant,
                participantAlt,
                JidHelper.Normalize);
            if (hints == null || hints.Count == 0)
            {
                return;
            }

            // RegisterAliasMappings takes one source for the batch; keep the first hint's
            // label (pairs are independent identity facts from the same envelope).
            var pairs = new List<KeyValuePair<string, string>>(hints.Count);
            string source = hints[0].Source;
            for (int i = 0; i < hints.Count; i++)
            {
                IncomingAliasHint hint = hints[i];
                if (hint == null ||
                    string.IsNullOrWhiteSpace(hint.LidJid) ||
                    string.IsNullOrWhiteSpace(hint.PnJid))
                {
                    continue;
                }

                pairs.Add(new KeyValuePair<string, string>(hint.LidJid, hint.PnJid));
            }

            if (pairs.Count > 0)
            {
                _aliasPersist.RegisterAliasMappings(pairs, source ?? "envelope");
            }
        }

        /// <inheritdoc />
        public void AcceptIncomingPushName(
            string rawName,
            bool isFromMe,
            string participantJid,
            string fromJid,
            bool isOffline)
        {
            if (string.IsNullOrEmpty(rawName))
            {
                return;
            }

            if (isFromMe)
            {
                CaptureSelfPushName(rawName, "message-echo");
            }

            string selfId = _jids.Self != null ? JidHelper.Normalize(_jids.Self.Id) : null;
            string senderJid = IncomingPushNameTarget.Resolve(
                isFromMe,
                selfId,
                JidHelper.Normalize(participantJid),
                JidHelper.Normalize(fromJid));
            if (string.IsNullOrEmpty(senderJid))
            {
                senderJid = JidHelper.Normalize(participantJid ?? fromJid);
            }

            if (string.IsNullOrEmpty(senderJid))
            {
                return;
            }

            _pushNames.TryGetValue(senderJid, out string existingName);
            string sanitized = SanitizeForResolvedName(rawName, senderJid);
            PushNameAccept accept = PushNameAcceptDecision.Decide(
                sanitized,
                existingName,
                senderJid,
                SelfChatNaming.IsSelfMarkerLabel(existingName) ||
                (_selfMarkers != null && _selfMarkers.IsMarkerLabel(existingName)));

            if (accept == PushNameAccept.IgnoreEmpty)
            {
                if (_jids.IsSelfLinked(senderJid))
                {
                    Debug.WriteLine(
                        "[ContactFacade] Explicit 'You' label observed for SELF JID " +
                        senderJid + ". Ignoring and keeping numeric identity.");
                }
                else
                {
                    Debug.WriteLine(
                        "[ContactFacade] Ignoring PushName 'You' for NON-SELF JID " +
                        senderJid + " (spoof prevention).");
                }

                return;
            }

            if (accept != PushNameAccept.Accept)
            {
                return;
            }

            RememberPersonName(senderJid, sanitized);
            if (!isOffline)
            {
                Debug.WriteLine(
                    "[ContactFacade] Updated contact name for " + senderJid +
                    " from message metadata: " + sanitized);
            }
        }

        /// <inheritdoc />
        public void NotifyInboundMessageNode(string fromJid, string notify, string participantJid)
        {
            ApplyStanzaNotifyPushName(fromJid, notify, participantJid);
        }

        /// <inheritdoc />
        public void ApplyStanzaNotifyPushName(string fromJid, string notify, string participantJid)
        {
            if (string.IsNullOrEmpty(fromJid) || string.IsNullOrEmpty(notify))
            {
                return;
            }

            string targetNotifyJid = fromJid;
            if (!string.IsNullOrEmpty(participantJid) &&
                JidHelper.IsGroupJid(fromJid))
            {
                targetNotifyJid = participantJid;
            }

            string normalizedNotifyTarget = JidHelper.Normalize(targetNotifyJid);
            string sanitizedNotify = SanitizeForResolvedName(notify, normalizedNotifyTarget);
            if (string.IsNullOrEmpty(sanitizedNotify))
            {
                Debug.WriteLine(
                    "[ContactFacade] Ignored notify pushname '" + notify +
                    "' for " + normalizedNotifyTarget);
                return;
            }

            string existingNotifyName;
            bool changed = !_pushNames.TryGetValue(normalizedNotifyTarget, out existingNotifyName) ||
                           !string.Equals(existingNotifyName, sanitizedNotify, StringComparison.Ordinal);
            if (changed)
            {
                RememberPersonName(normalizedNotifyTarget, sanitizedNotify);
                Debug.WriteLine(
                    "[ContactFacade] Captured pushname from notify: " +
                    targetNotifyJid + " -> " + sanitizedNotify);
            }

            if (_jids.IsSelfLinked(normalizedNotifyTarget))
            {
                CaptureSelfPushName(sanitizedNotify, "stanza-notify");
            }

            string canonical = _jids.GetCanonicalJid(normalizedNotifyTarget);
            _ = _ui.RunAsync(() =>
            {
                foreach (ChatItem chat in _chatRows.FindConversation(canonical))
                {
                    if (chat != null &&
                        PlaceholderChatLabel.IsPlaceholder(
                            chat.Name,
                            chat.JID,
                            SelfChatNaming.IsSelfMarkerLabel(chat.Name) ||
                            (_selfMarkers != null && _selfMarkers.IsMarkerLabel(chat.Name))))
                    {
                        chat.Name = sanitizedNotify;
                    }
                }
            });
        }

        private async Task PersistObservedPersonNameAsync(string jid, string displayName)
        {
            try
            {
                await _personStore.InitializeAsync().ConfigureAwait(false);
                await _personStore.UpsertIfChangedAsync(
                    jid,
                    displayName,
                    null,
                    JidHelper.TryPhoneFromJid(jid),
                    PersonSource.Observed).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ContactFacade] Person name upsert failed: " + ex.Message);
            }
        }

        /// <inheritdoc />
        public async Task ApplyRemoteContactUpdatesAsync(IReadOnlyList<AccountContactUpdate> contacts)
        {
            if (contacts == null || contacts.Count == 0)
            {
                return;
            }

            var aliasPairs = new List<KeyValuePair<string, string>>();
            var entries = new List<ResolvedNameEntry>();

            foreach (AccountContactUpdate contact in contacts)
            {
                if (contact == null)
                {
                    continue;
                }

                ContactUpdateReading reading = ContactUpdateFiling.Read(
                    contact.Id,
                    contact.Lid,
                    contact.PhoneNumber,
                    contact.Name,
                    contact.Notify);

                if (reading.HasAliasPair)
                {
                    aliasPairs.Add(new KeyValuePair<string, string>(reading.Lid, reading.Phone));
                }

                foreach (string address in reading.NameAddresses)
                {
                    ResolvedNameBatchPrep.Add(
                        entries,
                        address,
                        reading.Name,
                        isSubject: false,
                        JidHelper.Normalize,
                        _jids.GetCanonicalJid,
                        SanitizeForResolvedName);
                }
            }

            // Pairing before naming: LID rows borrow the PN label once the pair is known.
            if (aliasPairs.Count > 0)
            {
                _aliasPersist.RegisterAliasMappings(aliasPairs, "app-state-contact");
            }

            await ApplyResolvedNameBatchAsync(entries).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task NormalizePersistedChatNamesAsync()
        {
            int updated = await _nameRefresh.NormalizePersistedTitlesAsync().ConfigureAwait(false);
            if (updated > 0)
            {
                _catalog.Schedule();
            }
        }

        /// <inheritdoc />
        public Task ApplyResolvedNamesToChatListAsync() => _nameRefresh.ApplyAsync();

        /// <summary>
        /// Fills push / subject caches and relabels matching list rows once on the UI thread.
        /// </summary>
        public async Task ApplyResolvedNameBatchAsync(IList<ResolvedNameEntry> entries)
        {
            if (entries == null || entries.Count == 0)
            {
                return;
            }

            var byCanonical = new Dictionary<string, ResolvedNameEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (ResolvedNameEntry entry in entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Canonical))
                {
                    continue;
                }

                byCanonical[entry.Canonical] = entry;
            }

            await _ui.RunAsync(() =>
            {
                foreach (ResolvedNameEntry entry in entries)
                {
                    if (entry == null)
                    {
                        continue;
                    }

                    if (entry.IsSubject)
                    {
                        if (!GroupNameSyncBlacklist.ShouldCacheSyncedSubject(
                                entry.Name,
                                GroupMeaningfulLabel.Has(
                                    entry.Canonical,
                                    DuplicateChatRowPick.FirstNonBlankName(
                                        _chatRows.FindConversation(entry.Canonical)),
                                    jid =>
                                    {
                                        string cached;
                                        return _pushNames.TryGetValue(jid, out cached) ? cached : null;
                                    },
                                    IsMeaningfulChatLabel)))
                        {
                            continue;
                        }

                        _groupSubjects.Set(entry.Canonical, entry.Name);
                        if (!string.IsNullOrEmpty(entry.Normalized) &&
                            !string.Equals(entry.Normalized, entry.Canonical, StringComparison.OrdinalIgnoreCase))
                        {
                            _groupSubjects.Set(entry.Normalized, entry.Name);
                        }

                        continue;
                    }

                    _pushNames.Set(entry.Canonical, entry.Name);
                    if (!string.IsNullOrEmpty(entry.Normalized) &&
                        !string.Equals(entry.Normalized, entry.Canonical, StringComparison.OrdinalIgnoreCase))
                    {
                        _pushNames.Set(entry.Normalized, entry.Name);
                    }
                }

                foreach (ChatItem chat in _chatState.Chats)
                {
                    if (chat == null)
                    {
                        continue;
                    }

                    ResolvedNameEntry entry;
                    if (!byCanonical.TryGetValue(_jids.GetCanonicalJid(chat.JID), out entry))
                    {
                        continue;
                    }

                    if (entry.IsSubject)
                    {
                        bool incomingMeaningful = IsMeaningfulChatLabel(entry.Name, chat.JID, true);
                        bool existingMeaningful = IsMeaningfulChatLabel(chat.Name, chat.JID, true);
                        if (GroupNameSyncBlacklist.ShouldApplySyncedSubject(
                                entry.Name,
                                incomingMeaningful,
                                existingMeaningful))
                        {
                            chat.Name = entry.Name;
                        }
                    }
                    else if (!chat.IsGroup)
                    {
                        chat.Name = ResolveDisplayName(chat.JID, "chat");
                    }
                }
            }).ConfigureAwait(false);

            NotifyDisplayNamesUpdated();
            _chatState.NotifyDisplayNamesChanged();
        }

        private bool IsMeaningfulChatLabel(string label, string contextJid, bool isGroup) =>
            MeaningfulChatLabel.IsMeaningful(
                label,
                contextJid,
                isGroup,
                _selfMarkers.IsMarkerLabel(label));

        private void ScheduleAliasFollowUp(string source)
        {
            _aliasFollowUps.Request(source);
            _aliasFollowUpDebounce.Schedule(() => _ = RunAliasFollowUpAsync());
        }

        private async Task RunAliasFollowUpAsync()
        {
            if (!_aliasFollowUps.TryBegin(out AliasFollowUpBatch batch))
            {
                ScheduleAliasFollowUp(_aliasFollowUps.PendingSource);
                return;
            }

            string source = batch.Source;
            try
            {
                await _aliasPersist.PersistAliasesAsync("alias:" + source).ConfigureAwait(false);

                if (batch.AvatarTargets.Count > 0)
                {
                    await _ui.RunAsync(() =>
                    {
                        List<ChatItem> rows = AliasFollowUpAvatarRows.CollectMissing(
                            batch.AvatarTargets,
                            jid => _chatRows.FindConversation(jid));
                        foreach (ChatItem chat in rows)
                        {
                            RequestAvatarRefresh(chat, force: true);
                        }
                    }).ConfigureAwait(false);
                }

                ChatListDeduperResult result = await _deduper.RunAsync("alias:" + source)
                    .ConfigureAwait(false);
                if (result.Merged > 0 || result.NormalizedKeys > 0)
                {
                    _catalog.Schedule();
                    _chatState.NotifyDisplayNamesChanged();
                    NotifyDisplayNamesUpdated();
                    if (result.Merged > 0)
                    {
                        _identity.PersistNow("dedupe:alias:" + source);
                    }
                }
                else
                {
                    // Even with no fold, authors keyed by LID need the names-changed signal.
                    _chatState.NotifyDisplayNamesChanged();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ContactFacade] Alias follow-up failed: " + ex.Message);
                _aliasFollowUps.Restore(batch);
            }
            finally
            {
                _aliasFollowUps.End();
            }
        }

        public event EventHandler DisplayNamesUpdated;

        public void NotifyDisplayNamesUpdated() => DisplayNamesUpdated?.Invoke(this, EventArgs.Empty);

        public string ResolveDisplayName(string jid, string context)
        {
            return _displayNames.Resolve(jid, context);
        }

        /// <inheritdoc />
        public bool IsSelf(string jid) => SelfIdentity.IsSelf(jid, _jids);

        /// <inheritdoc />
        public string ResolvePlaceholderChatName(string jid)
        {
            string name = ResolveDisplayName(jid, "profile") ?? string.Empty;
            if (MeaningfulChatLabel.IsMeaningful(name, jid, isGroup: false))
            {
                return name;
            }

            string fallback = jid ?? string.Empty;
            int at = fallback.IndexOf('@');
            if (at > 0)
            {
                fallback = fallback.Substring(0, at);
            }

            return fallback;
        }

        /// <summary>
        /// Handed out read-only. The overlay is rebuilt wholesale by the address-book pass, and a
        /// caller that could write to it would be writing into state the next pass discards.
        /// </summary>
        public IReadOnlyDictionary<string, string> PhoneContactNamesByJid => _addressBookNames;

        public Task HydrateCachedAvatarUrisAsync(string reason)
        {
            return _avatars.HydrateCachedAsync(reason);
        }

        public void MarkAvatarImageLoadFailed(ChatItem chat, string reason)
        {
            if (chat == null)
            {
                return;
            }

            _avatarCache.DeleteIfCached(chat.AvatarUrl);

            // A broken local URI used to stay, and the 30-minute backoff then hid the picture
            // for the rest of the session. Clear it and ask again at once, including the PN/LID alias.
            chat.AvatarUrl = null;
            chat.AvatarFetchedAtUtc = null;
            chat.AvatarFetchFailedAtUtc = null;
            chat.AvatarFetchFailureReason = string.IsNullOrWhiteSpace(reason) ? "ui-image-failed" : reason;
            Debug.WriteLine($"[ContactFacade] UI avatar image load failed for {chat.JID}: {chat.AvatarFetchFailureReason}");
            RequestAvatarRefresh(chat, force: true);
            _catalog.Schedule();
        }

        public bool IsContactRefreshRunning => _names.IsRunning;

        public bool IsContactRefreshOnCooldown => _names.IsOnCooldown;

        /// <summary>
        /// Asks the server whether the number has an account. Falls back to the older search only
        /// when the socket could not answer at all: a definitive "no account" is an answer, and
        /// asking a second time would turn it into a maybe.
        /// </summary>
        public async Task<string> SearchContactAsync(string phoneNumber)
        {
            if (string.IsNullOrWhiteSpace(phoneNumber))
            {
                return null;
            }

            var result = await _directory.LookUpAsync(phoneNumber).ConfigureAwait(false);
            switch (result.Outcome)
            {
                case ContactLookupOutcome.Found:
                    return result.Jid;

                case ContactLookupOutcome.NoAccount:
                    return null;

                default:
                    return await SearchKnownContactsAsync(phoneNumber).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// The older path: a usync pass, then a prefix match over what it filed (an alias pair or a
        /// push name, so the match walks those two tables). Whatever the user typed is reduced to
        /// digits first; anything else in the string can only make the match miss.
        /// </summary>
        private async Task<string> SearchKnownContactsAsync(string phoneNumber)
        {
            string digits = PhoneNumberHelper.NormalizePhoneDigits(phoneNumber);
            if (string.IsNullOrEmpty(digits))
            {
                return null;
            }

            await _usync.ResolveAsync(new[] { digits }).ConfigureAwait(false);
            string jid = KnownJidByPhoneDigits.Find(digits, _aliases, _pushNames.Keys);
            Debug.WriteLine("[ContactFacade] Known-contact search for " + digits + ": " + (jid ?? "none"));
            return jid;
        }

        public string TryResolvePhone(string jid, string phoneDigits = null)
        {
            return ResolvePhone(jid, phoneDigits);
        }

        public bool CanAddToAddressBook(string jid, string phoneDigits = null)
        {
            if (JidHelper.IsGroupJid(jid))
            {
                return false;
            }

            if (IsSelfJid(jid))
            {
                return false;
            }

            string phone = ResolvePhone(jid, phoneDigits);
            if (string.IsNullOrEmpty(phone))
            {
                return false;
            }

            // Only the user's own agenda hides Add. Unison's People export is skipped
            // inside LoadPhoneContactNamesAsync / IsPhoneInAddressBook.
            return !_localContacts.IsPhoneInAddressBook(phone);
        }

        public async Task<bool> ShowAddToAddressBookAsync(
            string displayName,
            string phoneDigits,
            string photoUri = null,
            string jid = null)
        {
            string phone = ResolvePhone(jid, phoneDigits);
            if (string.IsNullOrEmpty(phone))
            {
                return false;
            }

            bool shown = await _localContacts.ShowSystemContactCardAsync(
                displayName,
                phone,
                photoUri).ConfigureAwait(false);
            if (!shown)
            {
                return false;
            }

            ScheduleOverlayRefreshWhenAppForegrounded();
            return true;
        }

        public async Task SetPublishContactsToWindowsAsync(bool enabled)
        {
            _localSettings.Set(LocalSettingsConstants.PublishContactsToWindowsEnabled, enabled);
            if (!enabled)
            {
                await _localContacts.ClearPublishedAppContactsAsync().ConfigureAwait(false);
                return;
            }

            await PublishWindowsContactsCoreAsync().ConfigureAwait(false);
        }

        public async Task RefreshContactNamesAsync(bool includeGroups = false, bool force = false)
        {
            await _names.RefreshAsync(includeGroups, force).ConfigureAwait(false);
            await TryPublishWindowsContactsAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// The People UI is shown and forgotten. Refreshing the agenda on the UI thread in the
        /// same turn dismisses the Windows 11 flyout. Wait until the window is foreground again
        /// (user closed the full card, or came back from People) via <see cref="IAppLifecycle"/>.
        /// </summary>
        private void ScheduleOverlayRefreshWhenAppForegrounded()
        {
            _ = RefreshOverlayAfterForegroundAsync();
        }

        private async Task RefreshOverlayAfterForegroundAsync()
        {
            try
            {
                await _lifecycle.WaitUntilForegroundAsync().ConfigureAwait(false);
                await RefreshPhoneContactOverlayAsync(force: true).ConfigureAwait(false);
                DisplayNamesUpdated?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ContactFacade] Overlay refresh after People card failed: " + ex.Message);
            }
        }

        private string ResolvePhone(string jid, string phoneDigits)
        {
            string phone = PhoneNumberHelper.NormalizePhoneDigits(phoneDigits);
            if (!string.IsNullOrEmpty(phone))
            {
                return phone;
            }

            foreach (string key in EnumerateLookupJids(jid))
            {
                phone = JidHelper.TryPhoneFromJid(key);
                if (!string.IsNullOrEmpty(phone))
                {
                    return phone;
                }

                Person person = _personStore != null ? _personStore.TryGetCached(key) : null;
                phone = PhoneNumberHelper.NormalizePhoneDigits(person?.Phone);
                if (!string.IsNullOrEmpty(phone))
                {
                    return phone;
                }
            }

            return null;
        }

        private IEnumerable<string> EnumerateLookupJids(string jid)
        {
            return ChatPinStoreKeys.For(jid, _jids);
        }

        private Person TryPerson(string jid)
        {
            if (_personStore == null || string.IsNullOrWhiteSpace(jid))
            {
                return null;
            }

            Person person = _personStore.TryGetCached(JidHelper.Normalize(jid));
            if (person != null)
            {
                return person;
            }

            string canonical = _jids.GetCanonicalJid(jid);
            if (!string.IsNullOrWhiteSpace(canonical) &&
                !string.Equals(canonical, jid, StringComparison.OrdinalIgnoreCase))
            {
                return _personStore.TryGetCached(canonical);
            }

            return null;
        }

        private bool IsSelfJid(string jid)
        {
            var profile = _jids.Self;
            if (profile == null || string.IsNullOrWhiteSpace(jid))
            {
                return false;
            }

            string canonical = _jids.GetCanonicalJid(jid);
            return string.Equals(
                       canonical,
                       _jids.GetCanonicalJid(profile.Id),
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       canonical,
                       _jids.GetCanonicalJid(profile.Lid),
                       StringComparison.OrdinalIgnoreCase);
        }

        private async Task TryPublishWindowsContactsAsync()
        {
            if (_localSettings == null ||
                !_localSettings.Get<bool>(LocalSettingsConstants.PublishContactsToWindowsEnabled))
            {
                return;
            }

            // Empty/short first pass (chats still loading) must be allowed to run again.
            bool snapshotLooksComplete = _lastWindowsPublishCount > 0;
            if (snapshotLooksComplete &&
                (DateTime.UtcNow - _lastWindowsPublishUtc).TotalSeconds < 30)
            {
                return;
            }

            await PublishWindowsContactsCoreAsync().ConfigureAwait(false);
        }

        private async Task PublishWindowsContactsCoreAsync()
        {
            if (Interlocked.CompareExchange(ref _windowsPublishBusy, 1, 0) != 0)
            {
                return;
            }

            try
            {
                // UI hop: copy ChatItem refs only. Resolve/name/phone/photo used to run inside
                // RunAsync and stalled Mobile for 10+s (ui-watchdog: PublishWindowsContactsCoreAsync).
                List<ChatItem> snapshot = null;
                await _ui.RunAsync(() =>
                {
                    var source = _chatState.Chats;
                    snapshot = new List<ChatItem>(source.Count);
                    for (int i = 0; i < source.Count; i++)
                    {
                        ChatItem chat = source[i];
                        if (chat != null)
                        {
                            snapshot.Add(chat);
                        }
                    }
                }).ConfigureAwait(false);

                List<AppContactExport> exports = WindowsContactExportBuilder.Build(
                    snapshot,
                    _jids,
                    IsSelfJid,
                    TryPerson,
                    jid => ResolvePhone(jid, null));

                await _localContacts.PublishAppContactsAsync(exports).ConfigureAwait(false);
                _lastWindowsPublishUtc = DateTime.UtcNow;
                _lastWindowsPublishCount = exports.Count;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ContactFacade] Windows People publish failed: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _windowsPublishBusy, 0);
            }
        }

        public Task<Dictionary<string, string>> SyncLocalContactsAsync(
            IEnumerable<string> directChatJids,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return _addressBook.SyncAsync(directChatJids, cancellationToken);
        }

        public Task RefreshPhoneContactOverlayAsync(bool force)
        {
            return _addressBook.RefreshAsync(force);
        }

        public async Task RefreshChatListAddressBookNamesAsync()
        {
            int mapped = await _addressBook.RefreshChatListAsync().ConfigureAwait(false);
            if (mapped > 0)
            {
                await _nameRefresh.ApplyAsync().ConfigureAwait(false);
            }
        }

        public Task ResolveMissingNamesAsync(bool force = false, bool includeGroupListing = true)
        {
            return _names.ResolveMissingAsync(force, includeGroupListing);
        }

        /// <inheritdoc />
        public async Task RunBackgroundResolutionAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            _syncStatus.Raise(SyncPhaseStatus.Format(SyncPhaseStatus.Names));
            try
            {
                // Groups listing is raised by NotifyBackgroundResolutionDue / enrichment
                // groups phase — do not embed QueryAllGroups inside names (Mobile 200s stall).
                await ResolveMissingNamesAsync(includeGroupListing: false).ConfigureAwait(false);

                string deferReason;
                if (_syncWork.ShouldDeferAvatarFetch(out deferReason))
                {
                    Debug.WriteLine(
                        "[ContactFacade] Deferring profile picture fetch until sync traffic settles: " +
                        deferReason);
                    _deferredAvatars.Schedule(deferReason);
                }
                else
                {
                    _deferredAvatars.ResetScheduleStreak();
                    _deferredAvatars.Cancel();
                    await RetrieveContactPicturesAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _syncStatus.Raise(null);
            }
        }

        /// <inheritdoc />
        public async Task NotifyBackgroundResolutionDueAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            await RunBackgroundResolutionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _groups.NotifyGroupListingDue();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "[ContactFacade] NotifyGroupListingDue failed: " + ex.Message);
            }
        }

        /// <inheritdoc />
        public void TriggerBackgroundResolution() => _backgroundResolution.Trigger();

        /// <inheritdoc />
        public void TriggerDeferredAvatarRetry()
        {
            _ = Task.Run(() => RunDeferredAvatarRetryAsync(CancellationToken.None));
        }

        /// <summary>
        /// Pictures (+ group listing nudge) after a deferred-avatar timer. Skips
        /// <see cref="ResolveMissingNamesAsync"/> so a stuck deferral does not re-walk
        /// every chat name every 20s on Mobile.
        /// </summary>
        private async Task RunDeferredAvatarRetryAsync(CancellationToken cancellationToken)
        {
            try
            {
                string deferReason;
                if (_syncWork.ShouldDeferAvatarFetch(out deferReason))
                {
                    Debug.WriteLine(
                        "[ContactFacade] Deferred avatar retry still blocked: " + deferReason);
                    _deferredAvatars.Schedule(deferReason);
                    return;
                }

                _deferredAvatars.ResetScheduleStreak();
                _deferredAvatars.Cancel();
                await RetrieveContactPicturesAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    _groups.NotifyGroupListingDue();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        "[ContactFacade] NotifyGroupListingDue after avatar retry failed: " +
                        ex.Message);
                }
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "[ContactFacade] Deferred avatar retry failed: " + ex.Message);
            }
        }

        /// <inheritdoc />
        public void CancelBackgroundResolution() => _backgroundResolution.Cancel();

        /// <inheritdoc />
        public async Task RunDeferredStartupContactPassAsync(bool resolveMissingNames = false)
        {
            await HydrateCachedAvatarUrisAsync("deferred-startup").ConfigureAwait(false);

            try
            {
                await RefreshPhoneContactOverlayAsync(force: true).ConfigureAwait(false);
                await _nameRefresh.ApplyAsync().ConfigureAwait(false);
                _catalog.Schedule();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "[ContactFacade] Address-book overlay after bootstrap failed: " + ex.Message);
            }

            if (resolveMissingNames)
            {
                _ = ResolveMissingNamesAsync();
            }
        }

        public async Task RetrieveContactPicturesAsync(CancellationToken token = default(CancellationToken))
        {
            await _avatars.RetrieveBatchAsync(token).ConfigureAwait(false);
            await TryPublishWindowsContactsAsync().ConfigureAwait(false);
        }

        public Task HydrateGroupMemberAvatarsAsync(string groupJid)
        {
            return _roster.HydrateAsync(groupJid);
        }

        public Task HydrateGroupMemberAvatarsForJidsAsync(string groupJid, IReadOnlyList<string> memberJids)
        {
            return _roster.HydrateVisibleAsync(groupJid, memberJids);
        }

        public bool IsHydratingGroupMemberAvatars => _roster.IsHydrating;

        public void RequestAvatarRefresh(ChatItem chat, bool force = false)
        {
            _avatars.RequestRefresh(chat, force);
        }

        /// <summary>Clears the "already attempted this session" marker so a JID can be retried at once.</summary>
        private void ClearAvatarAttempted(string jid)
        {
            _avatars.ClearAttempted(jid);
        }

        /// <summary>
        /// Records where an avatar was cached. Small enough to stay here: it is one write to the
        /// person store and reads none of the state the parts own.
        /// </summary>
        public async Task NotifyAvatarCachedAsync(string jid, string localAvatarUrl)
        {
            if (string.IsNullOrWhiteSpace(jid) || string.IsNullOrWhiteSpace(localAvatarUrl))
            {
                return;
            }

            try
            {
                await _personStore.InitializeAsync().ConfigureAwait(false);
                await _personStore.UpsertIfChangedAsync(
                    JidHelper.Normalize(jid),
                    null,
                    localAvatarUrl.Trim(),
                    JidHelper.TryPhoneFromJid(jid),
                    PersonSource.Observed).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ContactFacade] Avatar Person upsert failed: " + ex.Message);
            }
        }
    }
}
