using System;
using Unison.Core.Contracts;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Shared "is this JID the logged-in account?" checks for timeline labels (You / Você).
    /// </summary>
    public static class SelfIdentity
    {
        public static bool IsSelf(
            string participantJid,
            IJidResolver jids,
            Profile me = null)
        {
            if (string.IsNullOrWhiteSpace(participantJid) || jids == null)
            {
                return false;
            }

            me = me ?? jids.Self;
            if (me == null)
            {
                return false;
            }

            string probe = jids.GetCanonicalJid(participantJid)
                           ?? JidHelper.Normalize(participantJid);
            string probeNorm = JidHelper.Normalize(participantJid) ?? participantJid;
            if (string.IsNullOrWhiteSpace(probe) && string.IsNullOrWhiteSpace(probeNorm))
            {
                return false;
            }

            if (Matches(probe, probeNorm, me.Id, jids)
                || Matches(probe, probeNorm, me.Lid, jids))
            {
                return true;
            }

            string probePhone = JidHelper.TryPhoneFromJid(probeNorm)
                                ?? JidHelper.TryPhoneFromJid(probe);
            string selfPhone = !string.IsNullOrWhiteSpace(me.Phone)
                ? PhoneNumberHelper.NormalizePhoneDigits(me.Phone)
                : PhoneNumberHelper.NormalizePhoneDigits(JidHelper.TryPhoneFromJid(me.Id));
            return !string.IsNullOrWhiteSpace(probePhone) &&
                   !string.IsNullOrWhiteSpace(selfPhone) &&
                   string.Equals(probePhone, selfPhone, StringComparison.Ordinal);
        }

        public static bool Matches(
            string probeCanonical,
            string probeNormalized,
            string selfRaw,
            IJidResolver jids)
        {
            if (string.IsNullOrWhiteSpace(selfRaw))
            {
                return false;
            }

            string selfNorm = JidHelper.Normalize(selfRaw) ?? selfRaw;
            string selfCanonical = jids != null
                ? (jids.GetCanonicalJid(selfRaw) ?? selfNorm)
                : selfNorm;

            return (!string.IsNullOrWhiteSpace(probeNormalized) &&
                    string.Equals(probeNormalized, selfNorm, StringComparison.OrdinalIgnoreCase))
                   || (!string.IsNullOrWhiteSpace(probeCanonical) &&
                       string.Equals(probeCanonical, selfCanonical, StringComparison.OrdinalIgnoreCase))
                   || (!string.IsNullOrWhiteSpace(probeCanonical) &&
                       string.Equals(probeCanonical, selfNorm, StringComparison.OrdinalIgnoreCase));
        }
    }
}
