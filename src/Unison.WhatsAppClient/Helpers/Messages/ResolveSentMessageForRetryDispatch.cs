// =============================================================================
// ResolveSentMessageForRetryDispatch
//
// Body of ResolveSentMessageForRetryAsync. Persisted lookup stays on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class ResolveSentMessageForRetryDispatch
    {
        public static async Task<global::Proto.Message> RunAsync(
            IResolveSentMessageForRetryHost host,
            string remoteJid,
            string messageId)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (string.IsNullOrEmpty(remoteJid) ||
                string.IsNullOrEmpty(messageId) ||
                !host.HasMessageStore)
            {
                return null;
            }

            try
            {
                ChatMessage stored = await host.FindPersistedChatMessageAsync(
                        host.NormalizeJid(remoteJid),
                        messageId)
                    .ConfigureAwait(false);

                global::Proto.Message rebuilt = SentMessageRetryRebuild.TryCreate(stored);
                if (rebuilt == null &&
                    stored != null &&
                    stored.IsFromMe &&
                    stored.Kind != ChatMessageKind.Text)
                {
                    host.LogRetryLookupDeclined(messageId, stored.Kind);
                }

                return rebuilt;
            }
            catch (Exception ex)
            {
                host.LogRetryLookupFailed(messageId, ex.Message);
                return null;
            }
        }
    }
}
