using System;
using Unison.Core.Contracts.WhatsApp;
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
            IWhatsAppService whatsApp,
            Profile me = null)
        {
            if (string.IsNullOrWhiteSpace(participantJid) || whatsApp == null)
            {
                return false;
            }

            me = me ?? whatsApp.CurrentProfile;
            if (me == null)
            {
                return false;
            }

            string probe = whatsApp.GetCanonicalJid(participantJid)
                           ?? JidHelper.Normalize(participantJid);
            string probeNorm = JidHelper.Normalize(participantJid) ?? participantJid;
            if (string.IsNullOrWhiteSpace(probe) && string.IsNullOrWhiteSpace(probeNorm))
            {
                return false;
            }

            if (Matches(probe, probeNorm, me.Id, whatsApp)
                || Matches(probe, probeNorm, me.Lid, whatsApp))
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
            IWhatsAppService whatsApp)
        {
            if (string.IsNullOrWhiteSpace(selfRaw))
            {
                return false;
            }

            string selfNorm = JidHelper.Normalize(selfRaw) ?? selfRaw;
            string selfCanonical = whatsApp != null
                ? (whatsApp.GetCanonicalJid(selfRaw) ?? selfNorm)
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
