// =============================================================================
// IncomingPushNameTarget
//
// Which JID receives the push name on an inbound envelope. A message we sent
// carries our own name — attributing it to the conversation overwrites the
// contact. Extracted beside PushNameAcceptDecision.
// =============================================================================
namespace Unison.Core.Helpers
{
    public static class IncomingPushNameTarget
    {
        /// <summary>
        /// JID to bind the push name to, or null when none is usable.
        /// </summary>
        /// <param name="normalizedSelfJid">Account PN/LID already normalized; ignored when not from me.</param>
        /// <param name="normalizedParticipant">Group participant when present.</param>
        /// <param name="normalizedFromJid">Envelope from.</param>
        public static string Resolve(
            bool isFromMe,
            string normalizedSelfJid,
            string normalizedParticipant,
            string normalizedFromJid)
        {
            if (isFromMe)
            {
                return string.IsNullOrWhiteSpace(normalizedSelfJid) ? null : normalizedSelfJid;
            }

            if (!string.IsNullOrWhiteSpace(normalizedParticipant))
            {
                return normalizedParticipant;
            }

            return string.IsNullOrWhiteSpace(normalizedFromJid) ? null : normalizedFromJid;
        }
    }
}
