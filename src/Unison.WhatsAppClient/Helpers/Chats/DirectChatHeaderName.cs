// =============================================================================
// DirectChatHeaderName
//
// 1:1 conversation header title. The contact resolver wins when it knows a name,
// but on a cold tile/toast open it can run before names load and answer with the
// phone number. The header only re-resolves when ChatItem.Name changes, so the
// number stayed even though the chat row already carried the real name.
// =============================================================================
namespace Unison.WhatsAppClient.Helpers
{
    public static class DirectChatHeaderName
    {
        /// <summary>
        /// <paramref name="resolved"/> when it is a recognisable name, else the chat's own
        /// <paramref name="chatName"/> when that is, else <paramref name="resolved"/>.
        /// </summary>
        public static string Pick(string resolved, string chatName, string jid)
        {
            if (MeaningfulChatLabel.IsMeaningful(resolved, jid, isGroup: false))
            {
                return resolved;
            }

            if (MeaningfulChatLabel.IsMeaningful(chatName, jid, isGroup: false))
            {
                return chatName;
            }

            return resolved;
        }
    }
}
