// =============================================================================
// MeaningfulChatLabel
//
// Whether a chat-row label is something the user can recognise, as opposed to a
// phone echo, a masked number, a self-marker spoof, or a group id stand-in.
//
// Lived as IsMeaningfulChatLabel on WhatsAppService (eight call sites) with a
// weaker stand-in inside HistoryChatPreviewApplier. The two had already drifted:
// history skipped masked phones and phone echoes. One rule, under test.
//
// Self-marker recognition stays a boolean from the UWP head (localized "(You)").
// =============================================================================
using System;
using System.Linq;

namespace Unison.Core.Helpers
{
    public static class MeaningfulChatLabel
    {
        /// <summary>
        /// Whether <paramref name="label"/> is a usable display name for the chat at
        /// <paramref name="contextJid"/>.
        /// </summary>
        /// <param name="isSelfMarker">
        /// Whether the label is the localized self marker. Passed in because recognising
        /// "(You)" needs UWP resources.
        /// </param>
        public static bool IsMeaningful(
            string label,
            string contextJid,
            bool isGroup,
            bool isSelfMarker = false)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return false;
            }

            if (isSelfMarker)
            {
                return false;
            }

            string trimmed = label.Trim();
            if (ContactLabelSanitizer.IsMaskedPhoneLabel(trimmed))
            {
                return false;
            }

            if (trimmed.IndexOf('@') >= 0)
            {
                return false;
            }

            if (isGroup)
            {
                if (GroupNameSyncBlacklist.IsBlacklisted(trimmed))
                {
                    return false;
                }

                return !GroupIdPlaceholder.IsIdPlaceholder(trimmed, contextJid);
            }

            return !ContactLabelSanitizer.IsPhoneEcho(trimmed, contextJid);
        }
    }

    /// <summary>
    /// True when a group label is just the chat id: <c>120363…</c> or the legacy
    /// <c>phone-timestamp</c> user part. Those are placeholders, not subjects.
    /// </summary>
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
