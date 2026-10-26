// =============================================================================
// QuotedSenderNaming
//
// Names the author of a quoted bubble. The envelope only carries the JID;
// self vs directory vs sender-context fallback is decided here with callbacks
// so WinRT / account caches stay outside the helper.
// =============================================================================
using System;

namespace Unison.WhatsAppClient.Helpers
{
    public static class QuotedSenderNaming
    {
        public static string Resolve(
            string participantJid,
            Func<string, bool> isSelf,
            Func<string, string> resolveForQuote,
            Func<string, string> resolveForSender,
            string selfFallbackName)
        {
            if (string.IsNullOrEmpty(participantJid))
            {
                return null;
            }

            if (isSelf != null && isSelf(participantJid))
            {
                return selfFallbackName;
            }

            string name = resolveForQuote != null ? resolveForQuote(participantJid) : null;
            if (string.IsNullOrWhiteSpace(name) || name.IndexOf('@') >= 0)
            {
                name = resolveForSender != null ? resolveForSender(participantJid) : null;
            }

            return name;
        }
    }
}
