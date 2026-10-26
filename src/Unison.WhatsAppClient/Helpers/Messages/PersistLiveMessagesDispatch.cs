// =============================================================================
// PersistLiveMessagesDispatch
//
// Body of PersistLiveMessagesAsync. SQLite upsert stays on the host.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Models;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class PersistLiveMessagesDispatch
    {
        public static async Task RunAsync(
            IPersistLiveMessagesHost host,
            string chatJid,
            IList<ChatMessage> messages)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (!host.HasHistoryMessageStore ||
                string.IsNullOrWhiteSpace(chatJid) ||
                messages == null)
            {
                return;
            }

            var list = new List<ChatMessage>();
            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i] != null)
                {
                    list.Add(messages[i]);
                }
            }

            if (list.Count == 0)
            {
                return;
            }

            try
            {
                string normalized = host.NormalizeChatJid(chatJid);
                await host.UpsertLiveMessagesAsync(normalized, list).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                host.LogLivePersistFailed(ex.Message);
                throw;
            }
        }
    }
}
