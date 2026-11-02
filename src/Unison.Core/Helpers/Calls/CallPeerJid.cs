// =============================================================================
// CallPeerJid
//
// Pick the person/group JID from a CallOffer — outer stanza "from" is often @call.
// =============================================================================
using Unison.Core.Helpers;

namespace Unison.Core.Helpers.Calls
{
    public static class CallPeerJid
    {
        /// <summary>
        /// Contact (or group) identity for Recents / chrome. Prefers call-creator/From over
        /// the call-relay ChatId when the latter is <c>@call</c>.
        /// </summary>
        public static string Resolve(string chatId, string from, string groupJid, bool isGroup)
        {
            if (isGroup && !string.IsNullOrWhiteSpace(groupJid) && !JidHelper.IsCallRelayJid(groupJid))
            {
                return groupJid.Trim();
            }

            if (!string.IsNullOrWhiteSpace(from) && !JidHelper.IsCallRelayJid(from))
            {
                return from.Trim();
            }

            if (!string.IsNullOrWhiteSpace(chatId) && !JidHelper.IsCallRelayJid(chatId))
            {
                return chatId.Trim();
            }

            if (!string.IsNullOrWhiteSpace(from))
            {
                return from.Trim();
            }

            return string.IsNullOrWhiteSpace(chatId) ? null : chatId.Trim();
        }
    }
}
