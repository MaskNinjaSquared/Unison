using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ChatIdentityPersistenceTests
    {
        [Fact]
        public async Task Dispatch_persists_catalog_names_and_aliases()
        {
            var ui = new ImmediateDispatcher();
            var chats = new ChatStateStore(ui);
            var aliases = new JidAliasTable();
            var previews = new FakePreviewStore();
            var messages = new SpyMessageStore();

            chats.Chats.Add(new ChatItem { JID = "5511999999999@s.whatsapp.net", Name = "A" });
            chats.PushNames.TryAdd("5511999999999@s.whatsapp.net", "Alice");
            aliases["111111111111111@lid"] = "5511999999999@s.whatsapp.net";

            var persist = new ChatIdentityPersistence(
                chats,
                aliases,
                messages,
                previews,
                ui);

            await PersistChatIdentityStateDispatch.RunAsync(persist, "dedupe:test");

            Assert.Equal(1, previews.UpsertCount);
            Assert.False(previews.LastNotifyChunk);
            Assert.True(messages.ContactsSaved);
            Assert.True(messages.PhonesSaved);
            Assert.True(messages.AliasesSaved);
            Assert.Contains("5511999999999@s.whatsapp.net", messages.LastChatJids);
            Assert.Equal(
                "5511999999999@s.whatsapp.net",
                messages.LastAliases["111111111111111@lid"]);
            Assert.Equal("Alice", messages.LastContacts["5511999999999@s.whatsapp.net"]);
        }

        [Fact]
        public async Task Empty_catalog_is_noop()
        {
            var ui = new ImmediateDispatcher();
            var previews = new FakePreviewStore();
            var messages = new SpyMessageStore();
            var persist = new ChatIdentityPersistence(
                new ChatStateStore(ui),
                new JidAliasTable(),
                messages,
                previews,
                ui);

            await PersistChatIdentityStateDispatch.RunAsync(persist, "empty");

            Assert.Equal(0, previews.UpsertCount);
            Assert.False(messages.ContactsSaved);
        }

        private sealed class ImmediateDispatcher : IDispatcher
        {
            public Task RunAsync(Action action)
            {
                action?.Invoke();
                return Task.CompletedTask;
            }
        }

        private sealed class FakePreviewStore : IHistoryChatPreviewStore
        {
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
            public IReadOnlyList<string> LastChatJids { get; private set; }
            public Dictionary<string, string> LastContacts { get; private set; }
            public Dictionary<string, string> LastAliases { get; private set; }

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
                LastContacts = allContactNames;
                LastChatJids = new List<string>(chatJids);
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
                LastAliases = allAliases;
                return Task.CompletedTask;
            }

            public Task<Dictionary<string, string>> LoadJidAliasesAsync() =>
                Task.FromResult(new Dictionary<string, string>());
        }
    }
}
