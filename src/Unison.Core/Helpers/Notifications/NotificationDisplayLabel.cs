// =============================================================================
// NotificationDisplayLabel
//
// Toast / badge title and group author: prefer a resolved person name over a
// bare JID / @lid. Unsaved (push-name) contacts keep the WhatsApp "~ Name" mark.
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
        /// Unsaved push-name winners keep <c>~ Name</c>.
        /// </summary>
        public static string Prefer(string resolvedDisplayName, string currentLabel, string lookupJid)
        {
            bool isGroup = JidHelper.IsGroupJid(lookupJid);

            string toast = PreferUsable(resolvedDisplayName, lookupJid, isGroup);
            if (toast != null)
            {
                return toast;
            }

            toast = PreferUsable(currentLabel, lookupJid, isGroup);
            if (toast != null)
            {
                return toast;
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
            string currentBare = DisplayNameResolution.StripContactIndicator(currentLabel);
            if (!string.IsNullOrWhiteSpace(currentBare) && currentBare.IndexOf('@') < 0)
            {
                phone = PhoneNumberHelper.NormalizePhoneDigits(currentBare);
            }

            if (string.IsNullOrEmpty(phone))
            {
                phone = JidHelper.TryPhoneFromJid(lookupJid)
                    ?? JidHelper.TryPhoneFromJid(currentBare);
            }

            return GroupJoinDisplayLabel.Pick(
                DisplayNameResolution.StripContactIndicator(resolvedDisplayName),
                phone,
                lookupJid);
        }

        private static string PreferUsable(string label, string lookupJid, bool isGroup)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return null;
            }

            bool marked = DisplayNameResolution.HasUnsavedContactMark(label);
            string bare = DisplayNameResolution.StripContactIndicator(label);
            if (!IsUsableToastLabel(bare, lookupJid, isGroup))
            {
                return null;
            }

            return marked
                ? DisplayNameResolution.FormatUnsavedContactLabel(bare)
                : bare.Trim();
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
