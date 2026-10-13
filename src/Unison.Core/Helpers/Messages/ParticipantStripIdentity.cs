using System;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Which JID the group list strip should resolve against when the envelope carries both a
    /// participant and a participantAlt (LID addressing).
    /// </summary>
    /// <remarks>
    /// Group envelopes often put the LID on <c>participant</c> and the phone on
    /// <c>participantAlt</c>. Names and the Person table are usually keyed under the phone, so
    /// resolving the strip against the LID alone leaves the author blank while the bubble — which
    /// walks the roster and the alias table — already shows the person. Prefer the phone when it
    /// is known; keep the LID only when that is all we have.
    /// </remarks>
    public static class ParticipantStripIdentity
    {
        /// <summary>
        /// Phone JID when either side is a PN; otherwise the non-empty participant; otherwise null.
        /// </summary>
        public static string PreferForDisplay(string participantJid, string participantAltJid = null)
        {
            string participant = JidHelper.Normalize(participantJid);
            string alt = JidHelper.Normalize(participantAltJid);

            if (JidHelper.IsPhoneJid(participant))
            {
                return participant;
            }

            if (JidHelper.IsPhoneJid(alt))
            {
                return alt;
            }

            if (!string.IsNullOrWhiteSpace(participant))
            {
                return participant;
            }

            return string.IsNullOrWhiteSpace(alt) ? null : alt;
        }
    }
}
