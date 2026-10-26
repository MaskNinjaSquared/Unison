// =============================================================================
// ILoadPersistedChatCatalogHost
//
// Side effects for LoadPersistedChatCatalogDispatch (startup catalogue hydrate).
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts.WhatsApp
{
    public interface ILoadPersistedChatCatalogHost
    {
        IDisposable BeginPersistedChatsLoad();

        Task<IReadOnlyList<HistoryChatPreview>> ReadAllPreviewsAsync();

        Task ApplyPreviewsToChatListAsync(IReadOnlyList<HistoryChatPreview> stored);

        void LogLoaded(int previewCount);

        void NotifyHistorySyncAfterLoad();

        void LogLoadFailed(Exception error);
    }
}
