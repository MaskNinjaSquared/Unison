using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ForgetChatStorageHostTests
    {
        [Fact]
        public async Task Dispatch_through_host_tombstones_deletes_clears_persists()
        {
            var ui = new ImmediateDispatcher();
            var chats = new ChatStateStore(ui);
            chats.Chats.Add(new ChatItem { JID = "other@s.whatsapp.net", Name = "O" });

            var previews = new SpyPreviewStore();
            var history = new SpyHistoryStore();
            var messages = new SpyMessageStore();
            var host = new ForgetChatStorageHost(
                previews,
                history,
                messages,
                chats,
                ui);

            await ForgetChatStorageDispatch.RunAsync(
                host,
                "gone@s.whatsapp.net",
                new[] { "gone@lid" });

            Assert.True(previews.MarkedDeleted);
            Assert.Contains("gone@s.whatsapp.net", previews.LastMarkJids);
            Assert.Contains("gone@lid", previews.LastMarkJids);
            Assert.True(history.Deleted);
            Assert.True(messages.CacheCleared);
            Assert.Equal(1, previews.UpsertCount);
            Assert.False(previews.LastNotifyChunk);
        }

        [Fact]
        public async Task AppStateLocalHost_forwards_to_dispatch()
        {
            var forget = new RecordingForgetHost();
            var local = new AppStateLocalHost(forget);

            await local.ForgetChatStorageAsync("c@s.whatsapp.net", new[] { "a@lid" });

            Assert.True(forget.Ran);
        }

        private sealed class RecordingForgetHost : IForgetChatStorageHost
        {
            public bool Ran { get; private set; }

            public Task MarkPreviewsDeletedAsync(IReadOnlyList<string> jids)
            {
                Ran = true;
                return Task.CompletedTask;
            }

            public Task DeleteHistoryForKeysAsync(IReadOnlyList<string> jids) =>
                Task.CompletedTask;

            public void ClearMessageMemoryCache() { }

            public Task PersistCatalogAfterForgetAsync() => Task.CompletedTask;
        }

        private sealed class ImmediateDispatcher : IDispatcher
        {
            public Task RunAsync(Action action, string caller = null, string callerFile = null)
            {
                action?.Invoke();
                return Task.CompletedTask;
            }
        }

        private sealed class SpyPreviewStore : IHistoryChatPreviewStore
        {
            public bool MarkedDeleted { get; private set; }
            public IReadOnlyList<string> LastMarkJids { get; private set; }
            public int UpsertCount { get; private set; }
            public bool LastNotifyChunk { get; private set; }

            public int SchemaVersion => 1;
            public event EventHandler<HistoryChatPreviewChunkEventArgs> ChunkPersisted;

            public Task InitializeAsync() => Task.CompletedTask;

            public Task UpsertManyAsync(
                IReadOnlyList<HistoryChatPreview> rows,
                bool notifyChunk = true)
            {
                UpsertCount++;
                LastNotifyChunk = notifyChunk;
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<HistoryChatPreview>> GetAllAsync(string syncId = null) =>
                Task.FromResult<IReadOnlyList<HistoryChatPreview>>(Array.Empty<HistoryChatPreview>());

            public Task<HistoryChatPreview> TryGetAsync(string jid) =>
                Task.FromResult<HistoryChatPreview>(null);

            public Task<int> CountAsync(string syncId = null) => Task.FromResult(0);

            public Task ClearAsync(string reason = null) => Task.CompletedTask;

            public Task MarkDeletedAsync(IReadOnlyList<string> jids, DateTime deletedAtUtc)
            {
                MarkedDeleted = true;
                LastMarkJids = jids;
                return Task.CompletedTask;
            }

            public Task SetStatusAsync(IReadOnlyList<string> jids, ChatStatus status) =>
                Task.CompletedTask;
        }

        private sealed class SpyHistoryStore : IHistoryMessageStore
        {
            public bool Deleted { get; private set; }
            public int SchemaVersion => 1;
            public event EventHandler<HistoryMessageChunkEventArgs> ChunkPersisted;

            public Task InitializeAsync() => Task.CompletedTask;

            public Task<int> DeleteForChatKeysAsync(IReadOnlyList<string> chatJids)
            {
                Deleted = true;
                return Task.FromResult(0);
            }

            public Task UpsertManyAsync(IReadOnlyList<HistoryMessage> rows) =>
                Task.CompletedTask;

            public Task<HistoryWriteBatchResult> PersistWriteBatchAsync(
                HistoryMessageWriteBatch batch) =>
                Task.FromResult(new HistoryWriteBatchResult());

            public Task UpsertLiveMessagesAsync(
                string chatJid,
                IReadOnlyList<ChatMessage> messages) =>
                Task.CompletedTask;

            public Task UpsertReactionsAsync(IReadOnlyList<HistoryMessageReaction> reactions) =>
                Task.CompletedTask;

            public Task UpsertPinsAsync(IReadOnlyList<HistoryMessagePinUpdate> pins) =>
                Task.CompletedTask;

            public Task<HistoryMessage> GetAsync(string chatJid, string messageId) =>
                Task.FromResult<HistoryMessage>(null);

            public Task<IReadOnlyList<HistoryMessage>> GetForChatAsync(
                string chatJid,
                int limit = 100,
                DateTime? beforeUtc = null,
                string beforeMessageId = null) =>
                Task.FromResult<IReadOnlyList<HistoryMessage>>(Array.Empty<HistoryMessage>());

            public Task<IReadOnlyList<HistoryMessage>> GetForChatKeysAsync(
                IReadOnlyList<string> chatJids,
                int limit = 100,
                DateTime? beforeUtc = null,
                string beforeMessageId = null) =>
                Task.FromResult<IReadOnlyList<HistoryMessage>>(Array.Empty<HistoryMessage>());

            public Task<IReadOnlyList<HistoryMessage>> GetPinnedForChatAsync(
                string chatJid,
                int maxCount = 3) =>
                Task.FromResult<IReadOnlyList<HistoryMessage>>(Array.Empty<HistoryMessage>());

            public Task<IReadOnlyList<HistoryMessage>> GetPendingOutgoingAsync(string chatJid) =>
                Task.FromResult<IReadOnlyList<HistoryMessage>>(Array.Empty<HistoryMessage>());

            public Task<IReadOnlyList<HistoryMessage>> GetMediaForChatAsync(
                string chatJid,
                int limit = 400) =>
                Task.FromResult<IReadOnlyList<HistoryMessage>>(Array.Empty<HistoryMessage>());

            public Task<IReadOnlyList<HistoryMessage>> GetNewestPerChatAsync(
                IReadOnlyList<string> chatJids = null) =>
                Task.FromResult<IReadOnlyList<HistoryMessage>>(Array.Empty<HistoryMessage>());

            public Task<IReadOnlyList<HistoryMessageReaction>> GetReactionsForMessageAsync(
                string chatJid,
                string messageId) =>
                Task.FromResult<IReadOnlyList<HistoryMessageReaction>>(
                    Array.Empty<HistoryMessageReaction>());

            public Task<int> CountAsync(string syncId = null) => Task.FromResult(0);

            public Task ClearAsync(string reason = null) => Task.CompletedTask;

            public Task<IReadOnlyList<CachedMediaEntry>> ListCachedMediaAsync() =>
                Task.FromResult<IReadOnlyList<CachedMediaEntry>>(Array.Empty<CachedMediaEntry>());

            public Task MarkMediaDeletedAsync(IReadOnlyList<CachedMediaEntry> entries) =>
                Task.CompletedTask;

            public Task<bool> IsMediaDeletedAsync(string chatJid, string messageId) =>
                Task.FromResult(false);

            public Task MarkInboundReadAsync(IReadOnlyList<string> chatJids) =>
                Task.CompletedTask;
        }

        private sealed class SpyMessageStore : IMessageStore
        {
            public bool CacheCleared { get; private set; }

            public void ClearMemoryCache() => CacheCleared = true;

            public Task InitializeAsync() => Task.CompletedTask;
            public void QueuePendingIncoming(string chatJid, IEnumerable<ChatMessage> messages) { }
            public Task FlushPendingIncomingJournalAsync() => Task.CompletedTask;
            public Task<List<PendingIncomingRecord>> LoadPendingIncomingAsync() =>
                Task.FromResult(new List<PendingIncomingRecord>());
            public Task RemovePendingIncomingAsync(IEnumerable<string> messageIds) =>
                Task.CompletedTask;
            public Task SavePendingOutgoingAsync(string chatJid, ChatMessage message) =>
                Task.CompletedTask;
            public Task<List<ChatMessage>> LoadPendingOutgoingForChatAsync(string chatJid) =>
                Task.FromResult(new List<ChatMessage>());
            public Task<bool> AreMessagesPersistedAsync(
                string chatJid,
                IEnumerable<string> messageIds) =>
                Task.FromResult(false);
            public Task RemovePendingOutgoingAsync(IEnumerable<string> messageIds) =>
                Task.CompletedTask;
            public Task SaveMessageAsync(string chatJid, ChatMessage message) =>
                Task.CompletedTask;
            public Task SaveMessagesAsync(string chatJid, IEnumerable<ChatMessage> newMessages) =>
                Task.CompletedTask;
            public Task DeleteMessageAsync(string chatJid, string messageId) =>
                Task.CompletedTask;
            public Task<List<ChatMessage>> LoadMessagesPagedAsync(
                string chatJid,
                int skip,
                int take) =>
                Task.FromResult(new List<ChatMessage>());
            public Task<List<ChatMessage>> LoadPinnedMessagesAsync(
                string chatJid,
                int maxCount = 3) =>
                Task.FromResult(new List<ChatMessage>());
            public Task<ChatMessage> FindMessageByIdAsync(string chatJid, string messageId) =>
                Task.FromResult<ChatMessage>(null);
            public Task<List<ChatMessage>> LoadMessagesAsync(string chatJid) =>
                Task.FromResult(new List<ChatMessage>());
            public Task<int> GetMessageCountAsync(string chatJid) => Task.FromResult(0);
            public Task DeleteChatMessagesAsync(string chatJid) => Task.CompletedTask;
            public Task SaveChatsAsync(IEnumerable<ChatItem> chats) => Task.CompletedTask;
            public Task<List<ChatItem>> LoadChatsAsync() =>
                Task.FromResult(new List<ChatItem>());
            public Task<List<ChatItem>> RecoverChatsFromMessageFilesAsync() =>
                Task.FromResult(new List<ChatItem>());
            public Task WipeAllDataAsync() => Task.CompletedTask;
            public Task WipeChatsAndMessagesAsync() => Task.CompletedTask;
            public Task SaveContactNamesAsync(
                Dictionary<string, string> allContactNames,
                IEnumerable<string> chatJids) =>
                Task.CompletedTask;
            public Task<Dictionary<string, string>> LoadContactNamesAsync() =>
                Task.FromResult(new Dictionary<string, string>());
            public Task SavePhoneContactNamesAsync(
                Dictionary<string, string> allPhoneNames,
                IEnumerable<string> chatJids) =>
                Task.CompletedTask;
            public Task<Dictionary<string, string>> LoadPhoneContactNamesAsync() =>
                Task.FromResult(new Dictionary<string, string>());
            public Task SaveJidAliasesAsync(
                Dictionary<string, string> allAliases,
                IEnumerable<string> chatJids) =>
                Task.CompletedTask;
            public Task<Dictionary<string, string>> LoadJidAliasesAsync() =>
                Task.FromResult(new Dictionary<string, string>());
        }
    }
}
