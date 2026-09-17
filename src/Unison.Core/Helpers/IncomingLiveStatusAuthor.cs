// =============================================================================
// IncomingLiveStatusAuthor
//
// Who authored a status@broadcast row. Participant first; for from-me echoes
// fall back to the account id. No author means the status is not reportable.
// =============================================================================
namespace Unison.Core.Helpers
{
    public static class IncomingLiveStatusAuthor
    {
        /// <summary>
        /// Resolved author JID, or null when the status cannot be attributed.
        /// </summary>
        public static string Resolve(
            string normalizedParticipant,
            bool isFromMe,
            string normalizedSelfJid)
        {
            if (!string.IsNullOrWhiteSpace(normalizedParticipant))
            {
                return normalizedParticipant;
            }

            if (isFromMe && !string.IsNullOrWhiteSpace(normalizedSelfJid))
            {
                return normalizedSelfJid;
            }

            return null;
        }
    }
}
