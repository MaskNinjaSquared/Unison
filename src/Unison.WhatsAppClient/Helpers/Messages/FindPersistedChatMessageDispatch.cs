// =============================================================================
// FindPersistedChatMessageDispatch
//
// Body of FindPersistedChatMessageAsync. SQLite / mapper I/O stays on the host.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Models;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class FindPersistedChatMessageDispatch
    {
        public static async Task<ChatMessage> RunAsync(
            IFindPersistedChatMessageHost host,
            string chatJid,
            string messageId)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (!host.HasHistoryMessageStore ||
                string.IsNullOrWhiteSpace(chatJid) ||
                string.IsNullOrWhiteSpace(messageId))
            {
                return null;
            }

            IReadOnlyList<string> keys = ChatPinStoreKeys.For(chatJid, host.JidResolver);
            for (int i = 0; i < keys.Count; i++)
            {
                object row = await host.GetHistoryMessageRowAsync(keys[i], messageId)
                    .ConfigureAwait(false);
                ChatMessage mapped = host.MapHistoryRowToChatMessage(row);
                if (mapped != null)
                {
                    return mapped;
                }
            }

            return null;
        }
    }
}
