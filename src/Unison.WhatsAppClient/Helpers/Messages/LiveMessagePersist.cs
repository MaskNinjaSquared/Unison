// =============================================================================
// LiveMessagePersist
//
// Owns IPersistLiveMessagesHost — SQLite live upsert left WhatsAppService.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class LiveMessagePersist : IPersistLiveMessagesHost, ILiveMessagePersist
    {
        private readonly IHistoryMessageStore _history;

        public LiveMessagePersist(IHistoryMessageStore history)
        {
            _history = history ?? throw new ArgumentNullException(nameof(history));
        }

        public Task PersistAsync(string chatJid, IList<ChatMessage> messages) =>
            PersistLiveMessagesDispatch.RunAsync(this, chatJid, messages);

        public Task PersistLiveAsync(string chatJid, ChatMessage message) =>
            PersistAsync(chatJid, new[] { message });

        bool IPersistLiveMessagesHost.HasHistoryMessageStore => true;

        string IPersistLiveMessagesHost.NormalizeChatJid(string chatJid) =>
            JidHelper.Normalize(chatJid);

        Task IPersistLiveMessagesHost.UpsertLiveMessagesAsync(
            string normalizedChatJid,
            IReadOnlyList<ChatMessage> messages) =>
            _history.UpsertLiveMessagesAsync(normalizedChatJid, messages);

        void IPersistLiveMessagesHost.LogLivePersistFailed(string message) =>
            Debug.WriteLine("[LiveMessagePersist] SQLite live persist failed: " + message);
    }
}
