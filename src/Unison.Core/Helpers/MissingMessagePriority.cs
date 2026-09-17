// =============================================================================
// MissingMessagePriority
//
// Which missing-message placeholders to ask the phone about first. Peer 1:1 and
// own messages outrank group unknowns: a group can leave dozens of unresolved
// ids that are not worth burning the resend budget on.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public static class MissingMessagePriority
    {
        /// <summary>
        /// True when the candidate is from us or from a direct (PN/LID) chat — the drain prefers
        /// these over bare group placeholders.
        /// </summary>
        public static bool IsPeerOrSelf(bool isFromMe, string chatJid)
        {
            if (isFromMe)
            {
                return true;
            }

            string jid = chatJid ?? string.Empty;
            return jid.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase) ||
                   jid.EndsWith("@lid", StringComparison.OrdinalIgnoreCase);
        }
    }
}
