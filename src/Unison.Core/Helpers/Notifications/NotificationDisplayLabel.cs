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
        /// Toast title for a chat row. Community announce channels always use the localized
        /// “Avisos” / Announcements label — never the wire subject.
        /// For ordinary groups, <paramref name="resolvedDisplayName"/> must already be
        /// subjects-first (<c>IGroupNaming</c> / row <c>GetNameResolved</c>), not a contact
        /// push/agenda hit under the group JID.
        /// </summary>
        public static string PreferChatTitle(
            string resolvedDisplayName,
            string currentLabel,
            string lookupJid,
            bool isCommunityAnnounce,
            string communityAnnouncementsLabel)
        {
            if (isCommunityAnnounce && !string.IsNullOrWhiteSpace(communityAnnouncementsLabel))
            {
                return communityAnnouncementsLabel.Trim();
            }

            return Prefer(resolvedDisplayName, currentLabel, lookupJid);
        }

        /// <summary>
        /// Picks a toast-safe label: resolved name first, then a usable current label,
        /// then phone digits / JID fallback (same order as group-join lines).
        /// </summary>
        public static string Prefer(string resolvedDisplayName, string currentLabel, string lookupJid)
        {
            bool isGroup = JidHelper.IsGroupJid(lookupJid);

            string resolved = DisplayNameResolution.StripContactIndicator(resolvedDisplayName);
            if (IsUsableToastLabel(resolved, lookupJid, isGroup))
            {
                return resolved.Trim();
            }

            string current = DisplayNameResolution.StripContactIndicator(currentLabel);
            if (IsUsableToastLabel(current, lookupJid, isGroup))
            {
                return current.Trim();
            }

            // Groups: never toast the numeric id / bare JID — Prefer would otherwise treat
            // ContactDisplayNames' UserPartOf fallback as a "name" (all digits, no @).
            if (isGroup)
            {
                return string.Empty;
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

        private static bool IsUsableToastLabel(string label, string lookupJid, bool isGroup)
        {
            if (GroupJoinDisplayLabel.NeedsBetterName(label))
            {
                return false;
            }

            if (isGroup && GroupIdPlaceholder.IsIdPlaceholder(label, lookupJid))
            {
                return false;
            }

            return true;
        }
    }
}
