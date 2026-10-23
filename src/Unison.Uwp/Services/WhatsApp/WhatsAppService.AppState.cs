using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;

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
            ForgetChatInStorageAsync(canonicalJid, storeKeys);

        /// <summary>
        /// The persistent half of a chat deletion: tombstone the list preview, drop the stored
        /// messages, and take the chat out of the catalog the app rehydrates from.
        /// </summary>
        private async Task ForgetChatInStorageAsync(string canonical, IReadOnlyList<string> keys)
        {
            var jids = new List<string>();
            if (keys != null)
            {
                jids.AddRange(keys);
            }

            if (!jids.Any(k => string.Equals(k, canonical, StringComparison.OrdinalIgnoreCase)))
            {
                jids.Add(canonical);
            }

            if (_chatPreviews != null)
            {
                try
                {
                    await _chatPreviews.MarkDeletedAsync(jids, DateTime.UtcNow).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[WhatsAppService] Chat preview tombstone failed: " + ex.Message);
                }
            }

            if (_historyMessages != null)
            {
                try
                {
                    await _historyMessages.DeleteForChatKeysAsync(jids).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[WhatsAppService] Chat message delete failed: " + ex.Message);
                }
            }

            try
            {
                _messageStore?.ClearMemoryCache();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Message cache clear failed: " + ex.Message);
            }

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
