// =============================================================================
// NotificationDisplayLabel
//
// Toast / badge title and group author: prefer a resolved person name over a
// bare JID / @lid. Strips the unsaved "~" marker (toast is not the list strip).
// =============================================================================
using Unison.Core.Helpers.Groups;

namespace Unison.Core.Helpers.Notifications
{
    public static class NotificationDisplayLabel
    {
        /// <summary>
        /// Picks a toast-safe label: resolved name first, then a usable current label,
        /// then phone digits / JID fallback (same order as group-join lines).
        /// </summary>
        public static string Prefer(string resolvedDisplayName, string currentLabel, string lookupJid)
        {
            string resolved = DisplayNameResolution.StripContactIndicator(resolvedDisplayName);
            if (!GroupJoinDisplayLabel.NeedsBetterName(resolved))
            {
                return resolved.Trim();
            }

            string current = DisplayNameResolution.StripContactIndicator(currentLabel);
            if (!GroupJoinDisplayLabel.NeedsBetterName(current))
            {
                return current.Trim();
            }

            // Do not scrape digits out of an @lid / @s.whatsapp.net label — that yields the
            // LID user id, not a phone. Prefer the lookup JID's PN user part when present.
            string phone = null;
            if (!string.IsNullOrWhiteSpace(current) && current.IndexOf('@') < 0)
            {
                phone = PhoneNumberHelper.NormalizePhoneDigits(current);
            }

            if (string.IsNullOrEmpty(phone))
            {
                phone = JidHelper.TryPhoneFromJid(lookupJid)
                    ?? JidHelper.TryPhoneFromJid(current);
            }

            return GroupJoinDisplayLabel.Pick(resolved, phone, lookupJid);
        }
    }
}
