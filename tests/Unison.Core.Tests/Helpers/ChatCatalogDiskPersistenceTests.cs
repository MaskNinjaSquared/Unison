using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Proto;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ChatCatalogDiskPersistenceTests
    {
        [Fact]
        public async Task PersistFull_writes_previews_names_and_aliases()
        {
            var ui = new ImmediateDispatcher();
            var chats = new ChatStateStore(ui);
            var aliases = new JidAliasTable();
            var previews = new FakePreviewStore();
            var messages = new SpyMessageStore();

            chats.Chats.Add(new ChatItem { JID = "5511999999999@s.whatsapp.net", Name = "A" });
            chats.PushNames.TryAdd("5511999999999@s.whatsapp.net", "Alice");
            aliases["111111111111111@lid"] = "5511999999999@s.whatsapp.net";

            var disk = new ChatCatalogDiskPersistence(
                chats,
                aliases,
                messages,
                previews,
                ui,
                () => null);

            bool ok = await disk.PersistFullAsync();

            Assert.True(ok);
            Assert.Equal(1, previews.UpsertCount);
            Assert.True(messages.ContactsSaved);
            Assert.True(messages.PhonesSaved);
            Assert.True(messages.AliasesSaved);
        }

        [Fact]
        public async Task LoadPersistedChats_applies_previews_and_notifies_history()
        {
            var ui = new ImmediateDispatcher();
            var chats = new ChatStateStore(ui);
            var aliases = new JidAliasTable();
            var history = new SpyHistory();
            var previews = new FakePreviewStore
            {
                Rows = new List<HistoryChatPreview>
                {
                    new HistoryChatPreview
                    {
                        Jid = "5511888888888@s.whatsapp.net",
                        Name = "Bob",
                        LastMessageTimestampUtc = DateTime.UtcNow
                    }
                }
            };

            var disk = new ChatCatalogDiskPersistence(
                chats,
                aliases,
                new SpyMessageStore(),
                previews,
                ui,
                () => history);

            await disk.LoadPersistedChatsAsync();

            Assert.Single(chats.Chats);
            Assert.Equal("5511888888888@s.whatsapp.net", chats.Chats[0].JID);
            Assert.Equal(1, history.NotifyCount);
        }

        private sealed class ImmediateDispatcher : IDispatcher
        {
            public Task RunAsync(Action action, string caller = null, string callerFile = null)
            {
                action?.Invoke();
                return Task.CompletedTask;
            }
        }

        private sealed class SpyHistory : IHistoryService
        {
            public int NotifyCount { get; private set; }

            public event EventHandler<string> SyncStatusChanged;
            public event EventHandler<HistorySync> HistorySyncReceived;
            public event EventHandler<InitialSyncProgressEventArgs> InitialSyncProgress;
            public event EventHandler<HistoryChatPreviewChunkEventArgs> ChatPreviewChunkPersisted;
            public event EventHandler<HistoryMessageChunkEventArgs> HistoryMessageChunkPersisted;

            public bool IsLoadingPersistedChats => false;
            public bool IsInitialSyncSafeMode => false;
            public bool IsAwaitingFirstHistorySync => false;
            public bool PreferFrugalSyncBudget => false;
            public int InitialSyncProcessedConversations => 0;
            public int InitialSyncTotalConversations => 0;

            public Task TrackHistoryChunkStartedAsync(string syncType) => Task.CompletedTask;
            public Task TrackHistoryChunkCompletedAsync(string syncType, int conversationCount) =>
                Task.CompletedTask;
            public Task ResetHistorySqliteAsync(string reason = null) => Task.CompletedTask;
            public Task<HistorySqliteChunkResult> PersistHistorySqliteChunkAsync(HistorySync sync) =>
                Task.FromResult<HistorySqliteChunkResult>(null);
            public void NotifySqliteHistoryChunkApplied(string syncType, int conversationCount) { }
            public Task ResyncConversationsAsync(IProgress<ConversationResyncPhase> progress = null) =>
                Task.CompletedTask;
            public Task<bool> RequestFullHistoryOnDemandAsync(string reason = null, bool force = false) =>
                Task.FromResult(false);
            public Task<bool> EnsureChatPageAsync(string jid, int count) => Task.FromResult(false);
            public bool IsChatPagePending(string jid) => false;
            public void HandleOnDemandAck(string ackClass, string ackId, string ackError) { }
            public Task ApplyHistorySyncAsync(HistorySync sync) => Task.CompletedTask;

            public void NotifyHistorySyncReceived(HistorySync sync) => NotifyCount++;

            public void NotifySessionCleared() { }
            public void NotifyBackgroundHistorySyncBounced() { }
            public void NotifyCatchUpIdleBatch(
                string requestId,
                bool ackAccepted,
                int progressSignals,
                string triggerReason,
                int idleMs)
            {
            }
        }

        private sealed class FakePreviewStore : IHistoryChatPreviewStore
        {
            public IReadOnlyList<HistoryChatPreview> Rows { get; set; } =
                Array.Empty<HistoryChatPreview>();

            public int UpsertCount { get; private set; }

            public int SchemaVersion => 1;
            public event EventHandler<HistoryChatPreviewChunkEventArgs> ChunkPersisted;

            public Task InitializeAsync() => Task.CompletedTask;

            public Task UpsertManyAsync(
                IReadOnlyList<HistoryChatPreview> rows,
                bool notifyChunk = true)
            {
                UpsertCount++;
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<HistoryChatPreview>> GetAllAsync(string syncId = null) =>
                Task.FromResult(Rows);

            public Task<HistoryChatPreview> TryGetAsync(string jid) =>
                Task.FromResult<HistoryChatPreview>(null);

            public Task<int> CountAsync(string syncId = null) => Task.FromResult(Rows.Count);

            public Task ClearAsync(string reason = null) => Task.CompletedTask;

            public Task MarkDeletedAsync(IReadOnlyList<string> jids, DateTime deletedAtUtc) =>
                Task.CompletedTask;

            public Task SetStatusAsync(IReadOnlyList<string> jids, ChatStatus status) =>
                Task.CompletedTask;
        }

        private sealed class SpyMessageStore : IMessageStore
        {
            public bool ContactsSaved { get; private set; }
            public bool PhonesSaved { get; private set; }
            public bool AliasesSaved { get; private set; }

            public Task InitializeAsync() => Task.CompletedTask;
            public void ClearMemoryCache() { }
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
                IEnumerable<string> chatJids)
            {
                ContactsSaved = true;
                return Task.CompletedTask;
            }

            public Task<Dictionary<string, string>> LoadContactNamesAsync() =>
                Task.FromResult(new Dictionary<string, string>());

            public Task SavePhoneContactNamesAsync(
                Dictionary<string, string> allPhoneNames,
                IEnumerable<string> chatJids)
            {
                PhonesSaved = true;
                return Task.CompletedTask;
            }

            public Task<Dictionary<string, string>> LoadPhoneContactNamesAsync() =>
                Task.FromResult(new Dictionary<string, string>());

            public Task SaveJidAliasesAsync(
                Dictionary<string, string> allAliases,
                IEnumerable<string> chatJids)
            {
                AliasesSaved = true;
                return Task.CompletedTask;
            }

            public Task<Dictionary<string, string>> LoadJidAliasesAsync() =>
                Task.FromResult(new Dictionary<string, string>());
        }
    }
}
