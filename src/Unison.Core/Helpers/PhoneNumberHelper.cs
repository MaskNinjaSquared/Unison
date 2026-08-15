using System;
using System.Collections.Generic;
using System.Linq;

namespace Unison.Core.Helpers
{
    public static class PhoneNumberHelper
    {
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

        /// <summary>
        /// Builds lookup keys for a phone number (raw digits, UK local/international variants, last 10).
        /// </summary>
        public static IEnumerable<string> BuildPhoneKeys(string raw)
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
    }
}
