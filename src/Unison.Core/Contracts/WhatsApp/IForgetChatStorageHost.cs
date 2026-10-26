// =============================================================================
// IForgetChatStorageHost
//
// Side effects for ForgetChatStorageDispatch (durable chat-delete I/O).
// =============================================================================
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Unison.Core.Contracts.WhatsApp
{
    public interface IForgetChatStorageHost
    {
        Task MarkPreviewsDeletedAsync(IReadOnlyList<string> jids);

        Task DeleteHistoryForKeysAsync(IReadOnlyList<string> jids);

        void ClearMessageMemoryCache();

        Task PersistCatalogAfterForgetAsync();
    }
}
