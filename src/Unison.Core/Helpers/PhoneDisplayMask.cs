// =============================================================================
// PhoneDisplayMask
//
// Display form "+CC DDD LOCAL" for confirm-call dialogs. Brazil (55) uses a
// 2-digit area code and splits the local part with a hyphen (mobile 9 / land 8).
// Other lengths fall back to "+digits" so the dialog never shows a bare blob.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public static class PhoneDisplayMask
    {
        public static string Format(string phoneDigitsOrRaw)
        {
            string digits = PhoneNumberHelper.NormalizePhoneDigits(phoneDigitsOrRaw);
            if (string.IsNullOrEmpty(digits))
            {
                return string.Empty;
            }

            if (digits.StartsWith("55", StringComparison.Ordinal) && digits.Length >= 12)
            {
                string ddd = digits.Substring(2, 2);
                string local = digits.Substring(4);
                string maskedLocal = local.Length > 4
                    ? local.Substring(0, local.Length - 4) + "-" + local.Substring(local.Length - 4)
                    : local;
                return "+55 " + ddd + " " + maskedLocal;
            }

            return "+" + digits;
        }
    }
}
