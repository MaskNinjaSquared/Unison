// =============================================================================
// ILoadPersistedUiStateHost
//
// Side effects for LoadPersistedUiStateDispatch.
// =============================================================================
using System;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface ILoadPersistedUiStateHost
    {
        Task InitializeConnectionStateAsync();

        bool PersistedUiStateLoaded { get; }

        bool IsAuthRegistered { get; }

        Task EnterPersistedUiLoadLockAsync();

        void ExitPersistedUiLoadLock();

        Task LoadPersistedChatsAsync();

        void MarkPersistedUiStateLoaded();

        void WritePersistedUiLoadedJournal(int chatRowCount);

        int ChatRowCount { get; }

        Task HydrateCachedAvatarsBestEffortAsync();

        Task ReconcileChatPreviewsBestEffortAsync();
    }
}
