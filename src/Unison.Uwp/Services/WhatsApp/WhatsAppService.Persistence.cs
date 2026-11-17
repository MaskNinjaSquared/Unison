using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Models;
using Unison.Uwp.Client;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService
    {
        internal void EnableScheduledPersist(string reason) =>
            _catalogPersistence.EnableScheduledPersist(reason);

        /// <summary>
        /// Loads compact chat metadata for the UI. Safe to run after the connection has
        /// already started; only one caller performs the disk read.
        /// </summary>
        public Task LoadPersistedUiStateAsync() =>
            _startupHost.LoadPersistedUiStateAsync();

        private void QueueOfflineReplayMessageForPersist(string jid, ChatMessage message)
        {
            if (message == null || string.IsNullOrWhiteSpace(jid))
            {
                return;
            }

            _offlineReplayFlush.Enqueue(jid, new[] { message }, DateTime.UtcNow, true);
        }

        /// <summary>
        /// Persists current chats and contact metadata to disk.
        /// </summary>
        public Task<bool> PersistDataAsync() =>
            _catalogPersistence.PersistDataAsync();

        /// <summary>
        /// Schedules a debounced persist operation. Multiple calls within 3 seconds will batch into one save.
        /// </summary>
        internal void SchedulePersist() =>
            _catalogPersistence.SchedulePersist();

        private Task PersistChatCatalogAsync(IList<ChatItem> chats) =>
            PersistChatCatalogAsync(ChatCatalogSnapshot.Take(chats));

        private Task PersistChatCatalogAsync(ChatCatalogSnapshot snapshot) =>
            ChatCatalogPreviewPersist.UpsertAsync(_chatPreviews, snapshot);

        async Task IConversationResyncHost.ResyncChatListAppStateFlagsAsync()
        {
            try
            {
                var bridge = _socketSlot.Current as SocketBridge;
                if (bridge == null)
                {
                    return;
                }

                await bridge.ResyncAppStateReportingAllAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] ResyncChatListAppStateFlags failed: " + ex.Message);
            }
        }
    }
}
