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
        public async Task<Dictionary<string, string>> LoadPhoneContactNamesAsync()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var store = await ContactManager.RequestStoreAsync(ContactStoreAccessType.AllContactsReadOnly);
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
                Debug.WriteLine($"[LocalContactsService] Failed to load contacts: {ex.Message}");
            }

            return result;
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
