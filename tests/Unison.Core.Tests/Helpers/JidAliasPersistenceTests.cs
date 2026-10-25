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
    public sealed class JidAliasPersistenceTests
    {
        [Fact]
        public void RegisterAliasMappings_files_pair_and_registers_socket()
        {
            var aliases = new JidAliasTable();
            var socket = new FakeRegistrar();
            var ui = new ImmediateDispatcher();
            var persist = new JidAliasPersistence(
                aliases,
                socket,
                new StubMessageStore(),
                new ChatStateStore(ui),
                ui,
                () => null);

            persist.RegisterAliasMappings(
                new[]
                {
                    new KeyValuePair<string, string>(
                        "111111111111111@lid",
                        "5511999999999@s.whatsapp.net")
                },
                "test");

            Assert.Equal("5511999999999@s.whatsapp.net", aliases["111111111111111@lid"]);
            Assert.Equal("111111111111111@lid", socket.LastJidA);
            Assert.Equal("5511999999999@s.whatsapp.net", socket.LastJidB);
            Assert.Equal("test", socket.LastSource);
        }

        private sealed class FakeRegistrar : ISocketAliasRegistrar
        {
            public string LastJidA { get; private set; }
            public string LastJidB { get; private set; }
            public string LastSource { get; private set; }

            public void RegisterPair(string jidA, string jidB, string source)
            {
                LastJidA = jidA;
                LastJidB = jidB;
                LastSource = source;
            }

            public void RegisterPairs(
                IReadOnlyDictionary<string, string> aliases,
                string source)
            {
                // Bulk RegisterAliasMappings flushes via RegisterPairs, not RegisterPair.
                LastSource = source;
                if (aliases == null)
                {
                    return;
                }

                foreach (KeyValuePair<string, string> pair in aliases)
                {
                    if (pair.Key != null &&
                        pair.Key.EndsWith("@lid", System.StringComparison.OrdinalIgnoreCase))
                    {
                        LastJidA = pair.Key;
                        LastJidB = pair.Value;
                        return;
                    }
                }
            }

            public void HealOwnIdentityAfterUsync(string normalizedPn, string normalizedLid)
            {
            }
        }

        private sealed class ImmediateDispatcher : IDispatcher
        {
            public Task RunAsync(System.Action action, string caller = null, string callerFile = null)
            {
                action?.Invoke();
                return Task.CompletedTask;
            }
        }

        private sealed class StubMessageStore : IMessageStore
        {
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
