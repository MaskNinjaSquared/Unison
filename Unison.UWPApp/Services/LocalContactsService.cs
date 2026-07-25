using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.Contacts;

namespace Unison.UWPApp.Services
{
    public class LocalContactsService
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
                        foreach (var key in BuildPhoneKeys(phone?.Number))
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

        public static string NormalizePhoneDigits(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var digits = new string(value.Where(char.IsDigit).ToArray());
            if (string.IsNullOrEmpty(digits))
            {
                return null;
            }

            if (digits.StartsWith("00", StringComparison.Ordinal) && digits.Length > 2)
            {
                digits = digits.Substring(2);
            }

            return digits;
        }

        private static IEnumerable<string> BuildPhoneKeys(string raw)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var digits = NormalizePhoneDigits(raw);
            if (string.IsNullOrEmpty(digits))
            {
                return keys;
            }

            keys.Add(digits);

            if (digits.StartsWith("0", StringComparison.Ordinal) && digits.Length >= 10)
            {
                keys.Add("44" + digits.Substring(1));
            }

            if (digits.StartsWith("44", StringComparison.Ordinal) && digits.Length > 2)
            {
                keys.Add("0" + digits.Substring(2));
            }

            if (digits.Length > 10)
            {
                keys.Add(digits.Substring(digits.Length - 10));
            }

            return keys;
        }

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
