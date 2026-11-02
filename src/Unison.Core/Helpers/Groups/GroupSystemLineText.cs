// =============================================================================
// GroupSystemLineText
//
// Formats WhatsApp-style group system lines (join / added-by / leave / removed /
// subject change). Templates are supplied by the host (localized .resw).
// =============================================================================
using System;

namespace Unison.Core.Helpers.Groups
{
    public static class GroupSystemLineText
    {
        /// <summary>
        /// True when <paramref name="actorJid"/> is a distinct person from the participant
        /// (admin add/remove). Same address, blank actor, same canonical identity, or same
        /// phone digits → self join/leave wording.
        /// </summary>
        /// <remarks>
        /// Leave notifications often put the leaver as both author and child participant
        /// under different addressing (PN vs LID). Comparing only normalized strings then
        /// falsely yields "X removed X".
        /// </remarks>
        public static bool HasDistinctActor(
            string actorJid,
            string participantJid,
            Func<string, string> canonicalOf = null,
            string actorPhoneDigits = null,
            string participantPhoneDigits = null)
        {
            if (string.IsNullOrWhiteSpace(actorJid) || string.IsNullOrWhiteSpace(participantJid))
            {
                return false;
            }

            string aRaw = actorJid.Trim();
            string pRaw = participantJid.Trim();

            string aNorm = JidHelper.Normalize(aRaw) ?? aRaw;
            string pNorm = JidHelper.Normalize(pRaw) ?? pRaw;
            if (string.Equals(aNorm, pNorm, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string aId = CanonicalOr(aRaw, aNorm, canonicalOf);
            string pId = CanonicalOr(pRaw, pNorm, canonicalOf);
            if (string.Equals(aId, pId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string aPhone = FirstPhone(actorPhoneDigits, aId, aRaw);
            string pPhone = FirstPhone(participantPhoneDigits, pId, pRaw);
            if (!string.IsNullOrEmpty(aPhone) &&
                string.Equals(aPhone, pPhone, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        public static string JoinOrAdded(
            string joinTemplate,
            string addedByTemplate,
            string who,
            string actorLabel,
            string actorJid,
            string participantJid,
            Func<string, string> canonicalOf = null,
            string actorPhoneDigits = null,
            string participantPhoneDigits = null)
        {
            if (HasDistinctActor(
                    actorJid,
                    participantJid,
                    canonicalOf,
                    actorPhoneDigits,
                    participantPhoneDigits) &&
                !string.IsNullOrWhiteSpace(actorLabel) &&
                !string.IsNullOrWhiteSpace(addedByTemplate))
            {
                return Format(addedByTemplate, actorLabel, who);
            }

            return Format(joinTemplate ?? "{0} joined the group", who);
        }

        public static string LeaveOrRemoved(
            string leftTemplate,
            string removedByTemplate,
            string who,
            string actorLabel,
            string actorJid,
            string participantJid,
            Func<string, string> canonicalOf = null,
            string actorPhoneDigits = null,
            string participantPhoneDigits = null)
        {
            if (HasDistinctActor(
                    actorJid,
                    participantJid,
                    canonicalOf,
                    actorPhoneDigits,
                    participantPhoneDigits) &&
                !string.IsNullOrWhiteSpace(actorLabel) &&
                !string.IsNullOrWhiteSpace(removedByTemplate))
            {
                return Format(removedByTemplate, actorLabel, who);
            }

            return Format(leftTemplate ?? "{0} left", who);
        }

        /// <param name="withPreviousTemplate">
        /// Actor known: <c>{0}</c> actor, <c>{1}</c> previous, <c>{2}</c> new.
        /// </param>
        /// <param name="toOnlyTemplate">
        /// Actor known: <c>{0}</c> actor, <c>{1}</c> new.
        /// </param>
        /// <param name="anonymousWithPreviousTemplate">
        /// No actor: <c>{0}</c> previous, <c>{1}</c> new.
        /// </param>
        /// <param name="anonymousToOnlyTemplate">
        /// No actor: <c>{0}</c> new.
        /// </param>
        public static string SubjectChanged(
            string withPreviousTemplate,
            string toOnlyTemplate,
            string actorLabel,
            string previousSubject,
            string newSubject,
            string anonymousWithPreviousTemplate = null,
            string anonymousToOnlyTemplate = null)
        {
            string who = string.IsNullOrWhiteSpace(actorLabel) ? string.Empty : actorLabel.Trim();
            string next = newSubject ?? string.Empty;
            string previous = previousSubject ?? string.Empty;
            bool hasPrevious = !string.IsNullOrWhiteSpace(previous) &&
                !string.Equals(previous, next, StringComparison.Ordinal);

            if (string.IsNullOrWhiteSpace(who))
            {
                if (hasPrevious)
                {
                    return Format(
                        anonymousWithPreviousTemplate ?? "Group name changed from \"{0}\" to \"{1}\"",
                        previous,
                        next);
                }

                return Format(
                    anonymousToOnlyTemplate ?? "Group name changed to \"{0}\"",
                    next);
            }

            if (hasPrevious && !string.IsNullOrWhiteSpace(withPreviousTemplate))
            {
                return Format(withPreviousTemplate, who, previous, next);
            }

            return Format(toOnlyTemplate ?? "{0} changed the group name to \"{1}\"", who, next);
        }

        private static string CanonicalOr(
            string raw,
            string normalized,
            Func<string, string> canonicalOf)
        {
            if (canonicalOf == null)
            {
                return normalized;
            }

            string canonical = canonicalOf(raw);
            if (string.IsNullOrWhiteSpace(canonical))
            {
                canonical = canonicalOf(normalized);
            }

            return string.IsNullOrWhiteSpace(canonical) ? normalized : canonical.Trim();
        }

        private static string FirstPhone(string explicitDigits, string primaryJid, string fallbackJid)
        {
            return PhoneNumberHelper.NormalizePhoneDigits(explicitDigits)
                ?? JidHelper.TryPhoneFromJid(primaryJid)
                ?? JidHelper.TryPhoneFromJid(fallbackJid);
        }

        private static string Format(string template, params object[] args)
        {
            if (string.IsNullOrEmpty(template))
            {
                return string.Empty;
            }

            try
            {
                return string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    template,
                    args);
            }
            catch (FormatException)
            {
                return template;
            }
        }
    }
}
