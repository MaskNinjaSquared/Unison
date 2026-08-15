// =============================================================================
// ContactFacade
//
// The one implementation of IContactService: who the user knows, under which
// name, and with which picture.
//
// Contacts are not one feature but four, and each of them owns state the others
// must not touch - the session the LID resolver is bound to, the cooldown that
// keeps name resolution from taking over, the sets that stop the same avatar
// being fetched twice. Each of those lives with the operations that read it, in
// Contacts/, and this class is what makes them look like one feature from
// outside.
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
using Unison.Socket.Signal;
using Unison.Uwp.Services.Socket;

namespace Unison.Uwp.Services.WhatsApp.Contacts
{
    public sealed class ContactFacade : IContactService
    {
        private readonly IPersonStore _personStore;
        private readonly IWhatsAppService _whatsAppService;

        private readonly ContactDirectory _directory;
        private readonly AddressBookOverlay _addressBook;
        private readonly ContactNameResolver _names;
        private readonly ChatAvatarPolicy _avatars;

        internal ContactFacade(
            IWhatsAppSessionProvider sessions,
            ILocalContactsService localContacts,
            IPersonStore personStore,
            IWhatsAppService whatsAppService,
            LidMappingStore lidMappings)
        {
            if (sessions == null) throw new ArgumentNullException(nameof(sessions));
            if (localContacts == null) throw new ArgumentNullException(nameof(localContacts));
            if (lidMappings == null) throw new ArgumentNullException(nameof(lidMappings));

            _personStore = personStore ?? throw new ArgumentNullException(nameof(personStore));
            _whatsAppService = whatsAppService ?? throw new ArgumentNullException(nameof(whatsAppService));

            _directory = new ContactDirectory(sessions, lidMappings);
            _addressBook = new AddressBookOverlay(localContacts, personStore, whatsAppService);
            _names = new ContactNameResolver(whatsAppService, _addressBook, _directory);
            _avatars = new ChatAvatarPolicy(whatsAppService);

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

        public Task RefreshContactNamesAsync(bool includeGroups = false, bool force = false)
        {
            return _names.RefreshAsync(includeGroups, force);
        }

        public Task ResolveMissingNamesAsync()
        {
            return _names.ResolveMissingAsync();
        }

        public Task RetrieveContactPicturesAsync(CancellationToken token = default(CancellationToken))
        {
            return _avatars.RetrieveBatchAsync(token);
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
                    JidHelper.TryPhoneFromJid(jid)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ContactFacade] Avatar Person upsert failed: " + ex.Message);
            }
        }
    }
}
