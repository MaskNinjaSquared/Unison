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
using Unison.Uwp.Services.Socket;
using Windows.ApplicationModel.Core;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace Unison.Uwp.Services.WhatsApp.Contacts
{
    public sealed class ContactFacade : IContactService
    {
        private readonly IPersonStore _personStore;
        private readonly IWhatsAppService _whatsAppService;
        private readonly IDispatcher _ui;
        private readonly IChatCatalogPersistence _catalog;
        private readonly IJidResolver _jids;
        private readonly ILocalContactsService _localContacts;
        private readonly ILocalSettings _localSettings;
        private readonly JidAliasTable _aliases;
        private readonly DisplayNameTable _pushNames;
        private readonly DisplayNameTable _addressBookNames;
        private readonly IChatStateStore _chatState;
        private readonly ContactDisplayNames _displayNames;
        private readonly ChatNameRefresh _nameRefresh;

        private readonly ContactDirectory _directory;
        private readonly AddressBookOverlay _addressBook;
        private readonly ContactUsyncResolver _usync;
        private readonly ContactNameResolver _names;
        private readonly ChatAvatarPolicy _avatars;
        private readonly IAvatarCache _avatarCache;
        private readonly GroupRosterPolicy _roster;
        private DateTime _lastWindowsPublishUtc = DateTime.MinValue;
        private int _lastWindowsPublishCount;
        private int _windowsPublishBusy;

        internal ContactFacade(
            IWhatsAppSessionProvider sessions,
            ILocalContactsService localContacts,
            IPersonStore personStore,
            IWhatsAppService whatsAppService,
            LidMappingStore lidMappings,
            ILocalSettings localSettings,
            IJidResolver jids,
            IUsyncGate usyncGate,
            IGroupService groups,
            AvatarFetcher avatars,
            IChatStateStore chats,
            IAvatarReport avatarReport,
            IAvatarCache avatarCache,
            JidAliasTable aliases,
            DisplayNameTable pushNames,
            DisplayNameTable addressBookNames,
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
            IUsyncAliasHost usyncAliases)
        {
            _displayNames = displayNames ?? throw new ArgumentNullException(nameof(displayNames));
            _nameRefresh = nameRefresh ?? throw new ArgumentNullException(nameof(nameRefresh));
            if (selfMarkers == null) throw new ArgumentNullException(nameof(selfMarkers));
            if (syncStatus == null) throw new ArgumentNullException(nameof(syncStatus));
            if (syncWork == null) throw new ArgumentNullException(nameof(syncWork));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            if (sessions == null) throw new ArgumentNullException(nameof(sessions));
            if (localContacts == null) throw new ArgumentNullException(nameof(localContacts));
            if (lidMappings == null) throw new ArgumentNullException(nameof(lidMappings));
            if (usyncGate == null) throw new ArgumentNullException(nameof(usyncGate));
            if (groups == null) throw new ArgumentNullException(nameof(groups));
            if (avatars == null) throw new ArgumentNullException(nameof(avatars));
            _chatState = chats ?? throw new ArgumentNullException(nameof(chats));
            if (avatarReport == null) throw new ArgumentNullException(nameof(avatarReport));
            if (avatarCache == null) throw new ArgumentNullException(nameof(avatarCache));

            _personStore = personStore ?? throw new ArgumentNullException(nameof(personStore));
            _whatsAppService = whatsAppService ?? throw new ArgumentNullException(nameof(whatsAppService));
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
            _avatarCache = avatarCache;
            _localContacts = localContacts;
            _localSettings = localSettings ?? throw new ArgumentNullException(nameof(localSettings));
            _aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
            _pushNames = pushNames ?? throw new ArgumentNullException(nameof(pushNames));
            _addressBookNames = addressBookNames ?? throw new ArgumentNullException(nameof(addressBookNames));

            var labels = new ContactLabelSanitizer(selfMarkers);
            _directory = new ContactDirectory(sessions, lidMappings);
            _addressBook = new AddressBookOverlay(localContacts, personStore, jids, ui, addressBookNames, chats);
            _usync = new ContactUsyncResolver(
                usyncAliases ?? throw new ArgumentNullException(nameof(usyncAliases)),
                _directory,
                usyncGate,
                jids,
                pushNames,
                labels,
                chats,
                avatars,
                ui,
                nameRefresh,
                aliasMerge);
            _names = new ContactNameResolver(
                whatsAppService,
                _addressBook,
                _directory,
                _usync,
                jids,
                new PushNameLookup(pushNames, aliases, labels),
                groups,
                ui,
                catalog,
                chats,
                syncStatus,
                syncWork,
                sessions,
                nameRefresh);
            _avatars = new ChatAvatarPolicy(
                deferredAvatars ?? throw new ArgumentNullException(nameof(deferredAvatars)),
                jids,
                new ChatAvatarFetch(sessions, groups, jids, chats, avatarReport, avatars, avatarCache, ui, catalog),
                ui,
                catalog,
                chats,
                syncStatus,
                syncWork,
                sessions,
                syncBudget,
                workload ?? throw new ArgumentNullException(nameof(workload)),
                initialSync);
            _roster = new GroupRosterPolicy(groups, this, jids, ui, catalog, chats, syncWork, sessions, workload);

            // Both live as long as the app does, so there is nothing to unhook from.
            _whatsAppService.OnDisplayNamesUpdated += (s, e) =>
            {
                try
                {
                    DisplayNamesUpdated?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ContactFacade] DisplayNamesUpdated handler failed: " + ex.Message);
                }
            };
            _whatsAppService.OnAvatarCached += Client_OnAvatarCached;
            _whatsAppService.OnJidAliasResolved += Client_OnJidAliasResolved;
            _whatsAppService.OnAvatarRetryDue += Client_OnAvatarRetryDue;
        }

        /// <summary>
        /// Not awaited: the write swallows its own failures and the client has nothing to do with
        /// the result.
        /// </summary>
        private void Client_OnAvatarCached(object sender, AvatarCachedEventArgs e)
        {
            _ = NotifyAvatarCachedAsync(e.Jid, e.LocalAvatarUrl);
        }

        /// <summary>
        /// An earlier lookup may have asked under only the LID or only the PN and stamped a false
        /// "no-picture". Now that the pair is known, let the rows without an avatar retry at once.
        /// </summary>
        private void Client_OnJidAliasResolved(object sender, JidAliasResolvedEventArgs e)
        {
            ClearAvatarAttempted(e.LidJid);
            ClearAvatarAttempted(e.PhoneJid);
            ClearAvatarAttempted(_jids.GetCanonicalJid(e.PhoneJid));
        }

        /// <summary>
        /// Forced past the backoff: the failure it would honour was recorded under the half of the
        /// identity that could not answer. Runs on the UI thread the client raised it on, which
        /// <see cref="ChatAvatarPolicy.RequestRefresh"/> needs to read the live list.
        /// </summary>
        private void Client_OnAvatarRetryDue(object sender, AvatarRetryDueEventArgs e)
        {
            foreach (ChatItem chat in e.Chats)
            {
                RequestAvatarRefresh(chat, force: true);
            }
        }

        public event EventHandler DisplayNamesUpdated;

        public void NotifyDisplayNamesUpdated() => DisplayNamesUpdated?.Invoke(this, EventArgs.Empty);

        public string ResolveDisplayName(string jid, string context)
        {
            return _displayNames.Resolve(jid, context);
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
        /// same turn dismisses the Windows 11 flyout. Wait until this window is foreground again
        /// (user closed the full card, or came back from People).
        /// </summary>
        private void ScheduleOverlayRefreshWhenAppForegrounded()
        {
            var dispatcher = CoreApplication.MainView?.CoreWindow?.Dispatcher;
            if (dispatcher == null)
            {
                HookOverlayRefreshOnActivated();
                return;
            }

            _ = dispatcher.RunAsync(CoreDispatcherPriority.Normal, HookOverlayRefreshOnActivated);
        }

        private void HookOverlayRefreshOnActivated()
        {
            Window window;
            try
            {
                window = Window.Current;
            }
            catch
            {
                window = null;
            }

            if (window == null)
            {
                return;
            }

            WindowActivatedEventHandler handler = null;
            handler = async (sender, args) =>
            {
                if (args == null ||
                    args.WindowActivationState == CoreWindowActivationState.Deactivated)
                {
                    return;
                }

                window.Activated -= handler;
                try
                {
                    await RefreshPhoneContactOverlayAsync(force: true).ConfigureAwait(false);
                    DisplayNamesUpdated?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ContactFacade] Overlay refresh after People card failed: " + ex.Message);
                }
            };

            window.Activated += handler;
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
                var exports = new List<AppContactExport>();
                await _ui.RunAsync(() => CollectWindowsContactExports(exports))
                    .ConfigureAwait(false);
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

        private void CollectWindowsContactExports(List<AppContactExport> into)
        {
            if (into == null)
            {
                return;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ChatItem chat in _chatState.Chats)
            {
                if (chat == null ||
                    chat.IsGroup ||
                    chat.IsPersonal ||
                    JidHelper.IsGroupJid(chat.JID) ||
                    JidHelper.IsBroadcastJid(chat.JID) ||
                    IsSelfJid(chat.JID))
                {
                    continue;
                }

                string remoteId = _jids.GetCanonicalJid(chat.JID) ?? chat.JID;
                if (string.IsNullOrWhiteSpace(remoteId) || !seen.Add(remoteId))
                {
                    continue;
                }

                string name = chat.GetNameResolved(null);
                string photo = chat.GetAvatarUrl(preferHigh: true);
                if (string.IsNullOrWhiteSpace(photo))
                {
                    Person person = TryPerson(chat.JID);
                    photo = person != null ? person.AvatarUrl : null;
                }

                into.Add(new AppContactExport
                {
                    RemoteId = remoteId,
                    DisplayName = name,
                    PhoneDigits = ResolvePhone(chat.JID, null),
                    PhotoUri = photo
                });
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

        public Task ResolveMissingNamesAsync()
        {
            return _names.ResolveMissingAsync();
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
