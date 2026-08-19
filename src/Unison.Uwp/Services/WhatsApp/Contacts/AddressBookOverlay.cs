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
using Unison.Core.Models;

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
        /// Maps saved contact names onto the given JIDs and returns jid to display name.
        /// Updates Person.Name only (never the avatar) and promotes Source to AddressBook.
        /// </summary>
        public async Task<Dictionary<string, string>> SyncAsync(
            IEnumerable<string> directChatJids,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return await SyncAsync(directChatJids, extraPhones: null, cancellationToken).ConfigureAwait(false);
        }

        public async Task<Dictionary<string, string>> SyncAsync(
            IEnumerable<string> jids,
            IReadOnlyDictionary<string, string> extraPhones,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var overlay = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            cancellationToken.ThrowIfCancellationRequested();

            var phoneLookup = await _localContacts.LoadPhoneContactNamesAsync().ConfigureAwait(false);
            if (phoneLookup == null || phoneLookup.Count == 0)
            {
                Debug.WriteLine("[AddressBookOverlay] Phone contact overlay unavailable or empty");
                return overlay;
            }

            await _personStore.InitializeAsync().ConfigureAwait(false);

            var candidates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (jids != null)
            {
                foreach (string rawJid in jids)
                {
                    AddCandidate(candidates, rawJid, extraPhones);
                }
            }

            IReadOnlyList<Person> stored;
            try
            {
                stored = await _personStore.ListWithPhoneAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AddressBookOverlay] ListWithPhone failed: " + ex.Message);
                stored = Array.Empty<Person>();
            }

            foreach (var person in stored)
            {
                if (person == null || string.IsNullOrWhiteSpace(person.Jid))
                {
                    continue;
                }

                string jid = JidHelper.Normalize(person.Jid);
                if (string.IsNullOrEmpty(jid) || JidHelper.IsGroupJid(jid))
                {
                    continue;
                }

                if (!candidates.ContainsKey(jid) || string.IsNullOrEmpty(candidates[jid]))
                {
                    candidates[jid] = person.Phone;
                }
            }

            int personWrites = 0;
            var seenPhones = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string jid = pair.Key;
                string digits = PhoneNumberHelper.NormalizePhoneDigits(pair.Value);
                if (string.IsNullOrEmpty(digits))
                {
                    digits = JidHelper.TryPhoneFromJid(jid);
                }

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
                if (await TryUpsertAddressBookNameAsync(jid, display, digits).ConfigureAwait(false))
                {
                    personWrites++;
                }

                if (!seenPhones.Add(digits))
                {
                    continue;
                }

                IReadOnlyList<Person> samePhone;
                try
                {
                    samePhone = await _personStore.FindByPhoneAsync(digits).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[AddressBookOverlay] FindByPhone failed: " + ex.Message);
                    continue;
                }

                foreach (var other in samePhone)
                {
                    if (other == null || string.IsNullOrWhiteSpace(other.Jid))
                    {
                        continue;
                    }

                    string otherJid = JidHelper.Normalize(other.Jid);
                    if (string.IsNullOrEmpty(otherJid) ||
                        string.Equals(otherJid, jid, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    overlay[otherJid] = display;
                    if (await TryUpsertAddressBookNameAsync(otherJid, display, digits).ConfigureAwait(false))
                    {
                        personWrites++;
                    }
                }
            }

            Debug.WriteLine(
                "[AddressBookOverlay] Sync: overlay=" + overlay.Count +
                ", personWrites=" + personWrites);
            return overlay;
        }

        /// <summary>
        /// Rebuilds the in-memory overlay. When <paramref name="force"/> is false and an overlay
        /// already exists, skipped (reading the address book is not cheap). After bootstrap, pass
        /// true so live agenda names replace stale JSON / push names.
        /// </summary>
        public async Task RefreshAsync(bool force)
        {
            if (!force && _whatsAppService.PhoneContactNamesByJid.Count > 0)
            {
                return;
            }

            var extraPhones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<string> jids = null;
            await _whatsAppService.RunOnUiThreadAsync(() =>
            {
                jids = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var chat in _whatsAppService.Chats)
                {
                    if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
                    {
                        continue;
                    }

                    string canonical = _whatsAppService.GetCanonicalJid(chat.JID) ?? chat.JID;
                    if (chat.IsGroup)
                    {
                        if (chat.GroupMembers == null)
                        {
                            continue;
                        }

                        foreach (var member in chat.GroupMembers)
                        {
                            if (member == null || string.IsNullOrWhiteSpace(member.Jid))
                            {
                                continue;
                            }

                            string memberJid = JidHelper.Normalize(member.Jid);
                            if (string.IsNullOrEmpty(memberJid) || !seen.Add(memberJid))
                            {
                                continue;
                            }

                            jids.Add(memberJid);
                            string memberPhone = PhoneNumberHelper.NormalizePhoneDigits(member.PhoneNumber)
                                ?? JidHelper.TryPhoneFromJid(memberJid);
                            if (!string.IsNullOrEmpty(memberPhone))
                            {
                                extraPhones[memberJid] = memberPhone;
                            }
                        }

                        continue;
                    }

                    if (seen.Add(canonical))
                    {
                        jids.Add(canonical);
                    }
                }
            });

            var overlay = await SyncAsync(jids ?? new List<string>(), extraPhones).ConfigureAwait(false);
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

        private static void AddCandidate(
            Dictionary<string, string> candidates,
            string rawJid,
            IReadOnlyDictionary<string, string> extraPhones)
        {
            if (string.IsNullOrWhiteSpace(rawJid))
            {
                return;
            }

            string jid = JidHelper.Normalize(rawJid);
            if (string.IsNullOrEmpty(jid) || JidHelper.IsGroupJid(jid))
            {
                return;
            }

            string digits = null;
            if (extraPhones != null)
            {
                extraPhones.TryGetValue(jid, out digits);
                if (string.IsNullOrEmpty(digits))
                {
                    extraPhones.TryGetValue(rawJid, out digits);
                }
            }

            if (string.IsNullOrEmpty(digits))
            {
                digits = JidHelper.TryPhoneFromJid(jid);
            }

            candidates[jid] = digits;
        }

        private async Task<bool> TryUpsertAddressBookNameAsync(string jid, string display, string digits)
        {
            try
            {
                return await _personStore.UpsertIfChangedAsync(
                    jid,
                    display,
                    null,
                    digits,
                    PersonSource.AddressBook).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AddressBookOverlay] Person upsert failed for " + jid + ": " + ex.Message);
                return false;
            }
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

            foreach (string key in PhoneNumberHelper.BuildPhoneKeys(digits))
            {
                string mapped;
                if (phoneLookup.TryGetValue(key, out mapped) && !string.IsNullOrWhiteSpace(mapped))
                {
                    return mapped.Trim();
                }
            }

            return null;
        }
    }
}
