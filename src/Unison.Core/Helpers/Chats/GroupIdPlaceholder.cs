// =============================================================================
// GroupIdPlaceholder
//
// True when a group label is just the chat id: 120363… or the legacy
// phone-timestamp user part. Those are placeholders, not subjects.
// =============================================================================
using System;
using System.Linq;

namespace Unison.Core.Helpers
{
    public static class GroupIdPlaceholder
    {
        public static bool IsIdPlaceholder(string label, string groupJid)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return true;
            }

            string trimmed = label.Trim();
            if (trimmed.IndexOf('@') >= 0)
            {
                return true;
            }

            string bare = (groupJid ?? string.Empty).Split('@')[0];
            if (!string.IsNullOrEmpty(bare) &&
                string.Equals(trimmed, bare, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (trimmed.All(char.IsDigit))
            {
                return true;
            }

            string labelDigits = DigitsOnly(trimmed);
            string jidDigits = DigitsOnly(bare);
            bool hasLetters = trimmed.Any(char.IsLetter);
            return !hasLetters &&
                   jidDigits.Length >= 7 &&
                   string.Equals(labelDigits, jidDigits, StringComparison.Ordinal);
        }

        private static string DigitsOnly(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return new string(value.Where(char.IsDigit).ToArray());
        }
    }
}
