// =============================================================================
// ForgetChatStorageHost
//
// Owns IForgetChatStorageHost. Durable chat-delete I/O that used to live on
// WhatsAppService.AppState — same ownership shape as ChatIdentityPersistence.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class ForgetChatStorageHost : IForgetChatStorageHost
    {
        private readonly IHistoryChatPreviewStore _previews;
        private readonly IHistoryMessageStore _history;
        private readonly IMessageStore _messages;
        private readonly ChatStateStore _chats;
        private readonly IDispatcher _ui;

        public ForgetChatStorageHost(
            IHistoryChatPreviewStore previews,
            IHistoryMessageStore history,
            IMessageStore messages,
            ChatStateStore chats,
            IDispatcher ui)
        {
            _previews = previews ?? throw new ArgumentNullException(nameof(previews));
            _history = history ?? throw new ArgumentNullException(nameof(history));
            _messages = messages ?? throw new ArgumentNullException(nameof(messages));
            _chats = chats ?? throw new ArgumentNullException(nameof(chats));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        }

        /// <inheritdoc />
        public async Task MarkPreviewsDeletedAsync(IReadOnlyList<string> jids)
        {
            if (jids == null || jids.Count == 0)
            {
                return;
            }

            try
            {
                await _previews.MarkDeletedAsync(jids, DateTime.UtcNow).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "[ForgetChatStorageHost] Chat preview tombstone failed: " + ex.Message);
            }
        }

        /// <inheritdoc />
        public async Task DeleteHistoryForKeysAsync(IReadOnlyList<string> jids)
        {
            if (jids == null || jids.Count == 0)
            {
                return;
            }

            try
            {
                await _history.DeleteForChatKeysAsync(jids).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "[ForgetChatStorageHost] Chat message delete failed: " + ex.Message);
            }
        }

        /// <inheritdoc />
        public void ClearMessageMemoryCache()
        {
            try
            {
                _messages.ClearMemoryCache();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "[ForgetChatStorageHost] Message cache clear failed: " + ex.Message);
            }
        }

        /// <inheritdoc />
        public async Task PersistCatalogAfterForgetAsync()
        {
            try
            {
                List<ChatItem> snapshot = null;
                await _ui.RunAsync(() =>
                {
                    snapshot = _chats.Chats.Where(c => c != null).ToList();
                }).ConfigureAwait(false);

                await ChatCatalogPreviewPersist.UpsertAsync(
                    _previews,
                    ChatCatalogSnapshot.Take(snapshot ?? (IEnumerable<ChatItem>)Array.Empty<ChatItem>()))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "[ForgetChatStorageHost] Chat catalog persist failed: " + ex.Message);
            }
        }
    }
}
