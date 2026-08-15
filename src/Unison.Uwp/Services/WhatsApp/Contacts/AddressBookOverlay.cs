// =============================================================================
// AddressBookOverlay
//
// The names the user saved on the device, mapped onto the chats they belong to.
//
// These win over push names everywhere they exist, because someone who saved a
// contact as "Mom" expects to read "Mom" and not whatever name that account
// broadcasts. Holds no state: every call reads the address book and produces the
// mapping again, which is what makes it safe to call from anywhere.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;

namespace Unison.Uwp.Services.WhatsApp.Contacts
{
    internal sealed class AddressBookOverlay
    {
        private readonly ILocalContactsService _localContacts;
        private readonly IPersonStore _personStore;
        private readonly IWhatsAppService _whatsAppService;

        internal AddressBookOverlay(
            ILocalContactsService localContacts,
            IPersonStore personStore,
            IWhatsAppService whatsAppService)
        {
            _localContacts = localContacts ?? throw new ArgumentNullException(nameof(localContacts));
            _personStore = personStore ?? throw new ArgumentNullException(nameof(personStore));
            _whatsAppService = whatsAppService ?? throw new ArgumentNullException(nameof(whatsAppService));
        }

        /// <summary>
        /// Maps saved contact names onto the given direct chats and returns jid to display name.
        /// Also records what it learned in the person store.
        /// </summary>
        public async Task<Dictionary<string, string>> SyncAsync(
            IEnumerable<string> directChatJids,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var overlay = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (directChatJids == null)
            {
                return overlay;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var phoneLookup = await _localContacts.LoadPhoneContactNamesAsync().ConfigureAwait(false);
            if (phoneLookup == null || phoneLookup.Count == 0)
            {
                Debug.WriteLine("[AddressBookOverlay] Phone contact overlay unavailable or empty");
                return overlay;
            }

            await _personStore.InitializeAsync().ConfigureAwait(false);

            int personWrites = 0;
            foreach (string rawJid in directChatJids.Where(j => !string.IsNullOrWhiteSpace(j)))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string jid = JidHelper.Normalize(rawJid);
                if (string.IsNullOrEmpty(jid) || JidHelper.IsGroupJid(jid))
                {
                    continue;
                }

                string digits = JidHelper.TryPhoneFromJid(jid);
                if (string.IsNullOrEmpty(digits))
                {
                    continue;
                }

                string display = ResolveSavedName(phoneLookup, digits);
                if (string.IsNullOrWhiteSpace(display))
                {
                    continue;
                }

                overlay[jid] = display;

                try
                {
                    if (await _personStore.UpsertIfChangedAsync(jid, display, null, digits).ConfigureAwait(false))
                    {
                        personWrites++;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[AddressBookOverlay] Person upsert failed for " + jid + ": " + ex.Message);
                }
            }

            Debug.WriteLine(
                "[AddressBookOverlay] Sync: overlay=" + overlay.Count +
                ", personWrites=" + personWrites);
            return overlay;
        }

        /// <summary>
        /// Rebuilds the in-memory overlay for every direct chat. Skipped when one already exists
        /// and the caller did not insist, since reading the address book is not cheap.
        /// </summary>
        public async Task RefreshAsync(bool force)
        {
            if (!force && _whatsAppService.PhoneContactNamesByJid.Count > 0)
            {
                return;
            }

            List<string> directJids = null;
            await _whatsAppService.RunOnUiThreadAsync(() =>
            {
                directJids = _whatsAppService.Chats
                    .Where(c => c != null && !c.IsGroup && !string.IsNullOrWhiteSpace(c.JID))
                    .Select(c => _whatsAppService.GetCanonicalJid(c.JID) ?? c.JID)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            });

            var overlay = await SyncAsync(directJids ?? new List<string>());
            if (overlay == null || overlay.Count == 0)
            {
                Debug.WriteLine("[AddressBookOverlay] Overlay unavailable or empty; falling back to WhatsApp names");
                return;
            }

            int updates = 0;
            foreach (var pair in overlay)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                {
                    continue;
                }

                _whatsAppService.PhoneContactNamesByJid[pair.Key] = pair.Value.Trim();
                updates++;
            }

            Debug.WriteLine($"[AddressBookOverlay] Overlay refreshed: {updates} mapped JIDs");
        }

        /// <summary>
        /// Matches on the full number first, then on the last ten digits. The second pass is what
        /// bridges numbers saved without a country code, or with the extra ninth digit Brazil
        /// added to mobile lines and half the address books never gained.
        /// </summary>
        private static string ResolveSavedName(Dictionary<string, string> phoneLookup, string digits)
        {
            string byExact;
            if (phoneLookup.TryGetValue(digits, out byExact) && !string.IsNullOrWhiteSpace(byExact))
            {
                return byExact.Trim();
            }

            if (digits.Length <= 10)
            {
                return null;
            }

            string byLast10;
            return phoneLookup.TryGetValue(digits.Substring(digits.Length - 10), out byLast10) &&
                   !string.IsNullOrWhiteSpace(byLast10)
                ? byLast10.Trim()
                : null;
        }
    }
}
