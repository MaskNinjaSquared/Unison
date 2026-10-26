// =============================================================================
// GroupJoinDisplayLabel
//
// Picks the human label for "{0} joined the group" — never prefers a bare @jid
// when a phone or resolved name is available.
// =============================================================================
namespace Unison.Core.Helpers.Groups
{
    public static class GroupJoinDisplayLabel
    {
        /// <summary>
        /// True when the label is still an address / stand-in rather than a person name.
        /// </summary>
        public static bool NeedsBetterName(string label)
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

            return SelfChatNaming.IsKnownFallback(trimmed);
        }

        /// <summary>
        /// Prefer a resolved display name, then phone digits, then the JID user part / raw JID.
        /// </summary>
        public static string Pick(string resolvedDisplayName, string phoneDigits, string participantJid)
        {
            string who = (resolvedDisplayName ?? string.Empty).Trim();
            if (!NeedsBetterName(who))
            {
                return who;
            }

            if (!string.IsNullOrWhiteSpace(phoneDigits))
            {
                return phoneDigits.Trim();
            }

            string fromJid = JidHelper.TryPhoneFromJid(participantJid);
            if (!string.IsNullOrWhiteSpace(fromJid))
            {
                return fromJid;
            }

            if (!string.IsNullOrWhiteSpace(who))
            {
                return who;
            }

            return string.IsNullOrWhiteSpace(participantJid)
                ? string.Empty
                : participantJid.Trim();
        }
    }
}
