// =============================================================================
// ActiveChatCacheSideEffects
//
// Owns ActiveChat.Changed side effects: unload inactive message cache + clear
// sync banner. Hosts left WhatsAppService.
// =============================================================================
using System;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class ActiveChatCacheSideEffects :
        IOnActiveChatChangedHost,
        IUnloadMessageCacheIfInactiveHost,
        IDisposable
    {
        private readonly ActiveChat _activeChat;
        private readonly ChatStateStore _chats;
        private readonly SyncStatusChannel _syncStatus;
        private bool _disposed;

        public ActiveChatCacheSideEffects(
            ActiveChat activeChat,
            ChatStateStore chats,
            SyncStatusChannel syncStatus)
        {
            _activeChat = activeChat ?? throw new ArgumentNullException(nameof(activeChat));
            _chats = chats ?? throw new ArgumentNullException(nameof(chats));
            _syncStatus = syncStatus ?? throw new ArgumentNullException(nameof(syncStatus));
            _activeChat.Changed += OnChanged;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _activeChat.Changed -= OnChanged;
        }

        private void OnChanged(object sender, ActiveChatChangedEventArgs e) =>
            OnActiveChatChangedDispatch.Run(this, e.Previous, e.Current);

        void IOnActiveChatChangedHost.UnloadMessageCacheIfInactive(string jid) =>
            UnloadMessageCacheIfInactiveDispatch.Run(this, jid);

        void IOnActiveChatChangedHost.ClearSyncStatus() => _syncStatus.Raise(null);

        bool IUnloadMessageCacheIfInactiveHost.IsActiveChatJid(string jid) =>
            _activeChat.IsOpen(jid);

        string IUnloadMessageCacheIfInactiveHost.NormalizeJid(string jid) =>
            JidHelper.Normalize(jid);

        void IUnloadMessageCacheIfInactiveHost.RemoveMessagesByChatKey(string key) =>
            _chats.MessagesByChat.Remove(key);
    }
}
