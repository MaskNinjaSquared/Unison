// =============================================================================
// LoadPersistedUiStateOwner
//
// Owns ILoadPersistedUiStateHost. Init / catalogue load / avatar hydrate /
// preview reconcile stay as ctor ports.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class LoadPersistedUiStateOwner : ILoadPersistedUiStateHost
    {
        private readonly Func<Task> _initializeConnectionStateAsync;
        private readonly Func<bool> _isPersistedUiStateLoaded;
        private readonly Func<bool> _isAuthRegistered;
        private readonly Func<Task> _enterLoadLockAsync;
        private readonly Action _exitLoadLock;
        private readonly Func<Task> _loadPersistedChatsAsync;
        private readonly Action _markPersistedUiStateLoaded;
        private readonly Action<int> _writePersistedUiLoadedJournal;
        private readonly Func<int> _chatRowCount;
        private readonly Func<Task> _hydrateCachedAvatarsBestEffortAsync;
        private readonly Func<Task> _reconcileChatPreviewsBestEffortAsync;

        public LoadPersistedUiStateOwner(
            Func<Task> initializeConnectionStateAsync,
            Func<bool> isPersistedUiStateLoaded,
            Func<bool> isAuthRegistered,
            Func<Task> enterLoadLockAsync,
            Action exitLoadLock,
            Func<Task> loadPersistedChatsAsync,
            Action markPersistedUiStateLoaded,
            Action<int> writePersistedUiLoadedJournal,
            Func<int> chatRowCount,
            Func<Task> hydrateCachedAvatarsBestEffortAsync,
            Func<Task> reconcileChatPreviewsBestEffortAsync)
        {
            _initializeConnectionStateAsync = initializeConnectionStateAsync
                ?? throw new ArgumentNullException(nameof(initializeConnectionStateAsync));
            _isPersistedUiStateLoaded = isPersistedUiStateLoaded
                ?? throw new ArgumentNullException(nameof(isPersistedUiStateLoaded));
            _isAuthRegistered = isAuthRegistered
                ?? throw new ArgumentNullException(nameof(isAuthRegistered));
            _enterLoadLockAsync = enterLoadLockAsync
                ?? throw new ArgumentNullException(nameof(enterLoadLockAsync));
            _exitLoadLock = exitLoadLock
                ?? throw new ArgumentNullException(nameof(exitLoadLock));
            _loadPersistedChatsAsync = loadPersistedChatsAsync
                ?? throw new ArgumentNullException(nameof(loadPersistedChatsAsync));
            _markPersistedUiStateLoaded = markPersistedUiStateLoaded
                ?? throw new ArgumentNullException(nameof(markPersistedUiStateLoaded));
            _writePersistedUiLoadedJournal = writePersistedUiLoadedJournal
                ?? throw new ArgumentNullException(nameof(writePersistedUiLoadedJournal));
            _chatRowCount = chatRowCount
                ?? throw new ArgumentNullException(nameof(chatRowCount));
            _hydrateCachedAvatarsBestEffortAsync = hydrateCachedAvatarsBestEffortAsync
                ?? throw new ArgumentNullException(nameof(hydrateCachedAvatarsBestEffortAsync));
            _reconcileChatPreviewsBestEffortAsync = reconcileChatPreviewsBestEffortAsync
                ?? throw new ArgumentNullException(nameof(reconcileChatPreviewsBestEffortAsync));
        }

        public Task RunAsync() => LoadPersistedUiStateDispatch.RunAsync(this);

        Task ILoadPersistedUiStateHost.InitializeConnectionStateAsync() =>
            _initializeConnectionStateAsync();

        bool ILoadPersistedUiStateHost.PersistedUiStateLoaded =>
            _isPersistedUiStateLoaded();

        bool ILoadPersistedUiStateHost.IsAuthRegistered =>
            _isAuthRegistered();

        Task ILoadPersistedUiStateHost.EnterPersistedUiLoadLockAsync() =>
            _enterLoadLockAsync();

        void ILoadPersistedUiStateHost.ExitPersistedUiLoadLock() =>
            _exitLoadLock();

        Task ILoadPersistedUiStateHost.LoadPersistedChatsAsync() =>
            _loadPersistedChatsAsync();

        void ILoadPersistedUiStateHost.MarkPersistedUiStateLoaded() =>
            _markPersistedUiStateLoaded();

        void ILoadPersistedUiStateHost.WritePersistedUiLoadedJournal(int chatRowCount) =>
            _writePersistedUiLoadedJournal(chatRowCount);

        int ILoadPersistedUiStateHost.ChatRowCount =>
            _chatRowCount();

        Task ILoadPersistedUiStateHost.HydrateCachedAvatarsBestEffortAsync() =>
            _hydrateCachedAvatarsBestEffortAsync();

        Task ILoadPersistedUiStateHost.ReconcileChatPreviewsBestEffortAsync() =>
            _reconcileChatPreviewsBestEffortAsync();
    }
}
