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
        private readonly ILocalContactsService _localContacts;
        private readonly ILocalSettings _localSettings;

        private readonly ContactDirectory _directory;
        private readonly AddressBookOverlay _addressBook;
        private readonly ContactNameResolver _names;
        private readonly ChatAvatarPolicy _avatars;
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
            ILocalSettings localSettings)
        {
            if (sessions == null) throw new ArgumentNullException(nameof(sessions));
            if (localContacts == null) throw new ArgumentNullException(nameof(localContacts));
            if (lidMappings == null) throw new ArgumentNullException(nameof(lidMappings));

            _personStore = personStore ?? throw new ArgumentNullException(nameof(personStore));
            _whatsAppService = whatsAppService ?? throw new ArgumentNullException(nameof(whatsAppService));
            _localContacts = localContacts;
            _localSettings = localSettings ?? throw new ArgumentNullException(nameof(localSettings));

            _directory = new ContactDirectory(sessions, lidMappings);
            _addressBook = new AddressBookOverlay(localContacts, personStore, whatsAppService);
            _names = new ContactNameResolver(whatsAppService, _addressBook, _directory);
            _avatars = new ChatAvatarPolicy(whatsAppService);
            _roster = new GroupRosterPolicy(whatsAppService, this);

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
        }

        public event EventHandler DisplayNamesUpdated;

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
                    return await _whatsAppService.SearchContactAsync(phoneNumber).ConfigureAwait(false);
            }
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
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string> add = raw =>
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return;
                }

                string normalized = JidHelper.Normalize(raw);
                if (string.IsNullOrEmpty(normalized) || !seen.Add(normalized))
                {
                    return;
                }
            };

            add(jid);
            add(_whatsAppService.GetCanonicalJid(jid));
            string alias;
            string canonical = _whatsAppService.GetCanonicalJid(jid);
            if (!string.IsNullOrWhiteSpace(canonical) &&
                _whatsAppService.JidAlias != null &&
                _whatsAppService.JidAlias.TryGetValue(canonical, out alias))
            {
                add(alias);
            }

            return seen;
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

            string canonical = _whatsAppService.GetCanonicalJid(jid);
            if (!string.IsNullOrWhiteSpace(canonical) &&
                !string.Equals(canonical, jid, StringComparison.OrdinalIgnoreCase))
            {
                return _personStore.TryGetCached(canonical);
            }

            return null;
        }

        private bool IsSelfJid(string jid)
        {
            var profile = _whatsAppService.CurrentProfile;
            if (profile == null || string.IsNullOrWhiteSpace(jid))
            {
                return false;
            }

            string canonical = _whatsAppService.GetCanonicalJid(jid);
            return string.Equals(
                       canonical,
                       _whatsAppService.GetCanonicalJid(profile.Id),
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       canonical,
                       _whatsAppService.GetCanonicalJid(profile.Lid),
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
                await _whatsAppService.RunOnUiThreadAsync(() => CollectWindowsContactExports(exports))
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
            if (into == null || _whatsAppService.Chats == null)
            {
                return;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ChatItem chat in _whatsAppService.Chats)
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

                string remoteId = _whatsAppService.GetCanonicalJid(chat.JID) ?? chat.JID;
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

        public void RequestAvatarRefresh(ChatItem chat, bool force = false)
        {
            _avatars.RequestRefresh(chat, force);
        }

        public void ClearAvatarAttempted(string jid)
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
