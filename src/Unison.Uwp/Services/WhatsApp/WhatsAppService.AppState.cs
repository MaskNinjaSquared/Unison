using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService
    {
        /// <summary>
        /// Durable half of a chat deletion. RAM + badge are owned by
        /// <see cref="IChatService.ApplyAppStateDeleteChatAsync"/>.
        /// </summary>
        Task IAppStateLocalHost.ForgetChatStorageAsync(
            string canonicalJid,
            IReadOnlyList<string> storeKeys) =>
            ForgetChatStorageDispatch.RunAsync(this, canonicalJid, storeKeys);

        async Task IForgetChatStorageHost.MarkPreviewsDeletedAsync(IReadOnlyList<string> jids)
        {
            if (_chatPreviews == null)
            {
                return;
            }

            try
            {
                await _chatPreviews.MarkDeletedAsync(jids, DateTime.UtcNow).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Chat preview tombstone failed: " + ex.Message);
            }
        }

        async Task IForgetChatStorageHost.DeleteHistoryForKeysAsync(IReadOnlyList<string> jids)
        {
            if (_historyMessages == null)
            {
                return;
            }

            try
            {
                await _historyMessages.DeleteForChatKeysAsync(jids).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Chat message delete failed: " + ex.Message);
            }
        }

        void IForgetChatStorageHost.ClearMessageMemoryCache()
        {
            try
            {
                _messageStore?.ClearMemoryCache();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Message cache clear failed: " + ex.Message);
            }
        }

        async Task IForgetChatStorageHost.PersistCatalogAfterForgetAsync()
        {
            try
            {
                List<ChatItem> snapshot = null;
                await _ui.RunAsync(() => snapshot = Chats.Where(c => c != null).ToList());
                await PersistChatCatalogAsync(snapshot ?? new List<ChatItem>()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Chat catalog persist failed: " + ex.Message);
            }
        }
    }
}
