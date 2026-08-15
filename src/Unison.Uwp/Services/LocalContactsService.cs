using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Windows.ApplicationModel.Contacts;

namespace Unison.Uwp.Services
{
    public class LocalContactsService : ILocalContactsService
    {
        /// <summary>
        /// Set once the address book turns out to be unreachable on this machine, so the failure
        /// is paid for once. The overlay refresh runs repeatedly, and the store does not become
        /// available later in the session - retrying only reproduces the same throw.
        /// </summary>
        private bool _storeUnavailable;

        public async Task<Dictionary<string, string>> LoadPhoneContactNamesAsync()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (_storeUnavailable)
            {
                return result;
            }

            try
            {
                var store = await OpenStoreAsync();
                if (store == null)
                {
                    _storeUnavailable = true;
                    Debug.WriteLine("[LocalContactsService] No contact store on this device; phone names are unavailable");
                    return result;
                }

                var contacts = await store.FindContactsAsync();

                foreach (var contact in contacts)
                {
                    string displayName = BuildDisplayName(contact);
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        continue;
                    }

                    foreach (var phone in contact.Phones)
                    {
                        foreach (var key in PhoneNumberHelper.BuildPhoneKeys(phone?.Number))
                        {
                            if (!result.ContainsKey(key))
                            {
                                result[key] = displayName.Trim();
                            }
                        }
                    }
                }

                Debug.WriteLine($"[LocalContactsService] Loaded phone contact keys: {result.Count}");
            }
            catch (Exception ex)
            {
                _storeUnavailable = true;
                Debug.WriteLine($"[LocalContactsService] Failed to load contacts: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// The whole address book if we are allowed to read it, otherwise the contacts this app
        /// owns. Reading everything needs a restricted capability that a sideloaded or desktop
        /// build often does not have, and the platform reports that by throwing "element not
        /// found" rather than by returning null - which is why the first attempt is guarded
        /// instead of merely null-checked.
        /// </summary>
        private static async Task<ContactStore> OpenStoreAsync()
        {
            try
            {
                var store = await ContactManager.RequestStoreAsync(ContactStoreAccessType.AllContactsReadOnly);
                if (store != null)
                {
                    return store;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[LocalContactsService] The system address book is not readable: " + ex.Message);
            }

            try
            {
                return await ContactManager.RequestStoreAsync(ContactStoreAccessType.AppContactsReadWrite);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[LocalContactsService] No app contact store either: " + ex.Message);
                return null;
            }
        }

        /// <summary>Kept for call sites that used the static API before PhoneNumberHelper.</summary>
        public static string NormalizePhoneDigits(string value)
            => PhoneNumberHelper.NormalizePhoneDigits(value);

        private static string BuildDisplayName(Contact contact)
        {
            if (contact == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(contact.DisplayName))
            {
                return contact.DisplayName;
            }

            string first = contact.FirstName?.Trim();
            string last = contact.LastName?.Trim();
            if (!string.IsNullOrEmpty(first) && !string.IsNullOrEmpty(last))
            {
                return first + " " + last;
            }

            return first ?? last;
        }
    }
}
