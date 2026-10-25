// =============================================================================
// ChatCatalogPreviewPersist
//
// Shared UpsertMany path for history_chat_preview catalogue rows. Used by
// ChatIdentityPersistence and WhatsAppService until IChatCatalogFullPersistHost
// leaves the client.
// =============================================================================
using System.Threading.Tasks;
using Unison.Core.Contracts;

namespace Unison.WhatsAppClient.Helpers
{
    public static class ChatCatalogPreviewPersist
    {
        public static Task UpsertAsync(
            IHistoryChatPreviewStore previews,
            ChatCatalogSnapshot snapshot)
        {
            if (previews == null || snapshot == null || snapshot.Rows == null ||
                snapshot.Rows.Count == 0)
            {
                return Task.CompletedTask;
            }

            return previews.UpsertManyAsync(snapshot.Rows, notifyChunk: false);
        }
    }
}
