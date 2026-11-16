// =============================================================================
// IForgetChatStorageHost
//
// Side effects for ForgetChatStorageDispatch (durable chat-delete I/O).
// =============================================================================
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IForgetChatStorageHost
    {
        Task MarkPreviewsDeletedAsync(IReadOnlyList<string> jids);

        Task DeleteHistoryForKeysAsync(IReadOnlyList<string> jids);

        void ClearMessageMemoryCache();

        Task PersistCatalogAfterForgetAsync();
    }
}
