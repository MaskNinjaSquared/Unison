using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Models;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class PersistLiveMessagesDispatchTests
    {
        [Fact]
        public async Task Missing_store_is_noop()
        {
            var host = new FakeHost { HasStore = false };
            await PersistLiveMessagesDispatch.RunAsync(
                host,
                "a@s.whatsapp.net",
                new[] { new ChatMessage() });
            Assert.False(host.Upserted);
        }

        [Fact]
        public async Task Filters_null_messages()
        {
            var host = new FakeHost { HasStore = true };
            await PersistLiveMessagesDispatch.RunAsync(
                host,
                "a@s.whatsapp.net",
                new ChatMessage[] { null, new ChatMessage { Id = "1" } });
            Assert.True(host.Upserted);
            Assert.Single(host.UpsertList);
        }

        [Fact]
        public async Task Sql_failure_rethrows()
        {
            var host = new FakeHost { HasStore = true, ThrowOnUpsert = true };
            await Assert.ThrowsAsync<System.InvalidOperationException>(() =>
                PersistLiveMessagesDispatch.RunAsync(
                    host,
                    "a@s.whatsapp.net",
                    new[] { new ChatMessage() }));
            Assert.True(host.LoggedFail);
        }

        private sealed class FakeHost : IPersistLiveMessagesHost
        {
            public bool HasStore;
            public bool ThrowOnUpsert;
            public bool Upserted;
            public bool LoggedFail;
            public List<ChatMessage> UpsertList;

            public bool HasHistoryMessageStore => HasStore;

            public string NormalizeChatJid(string chatJid) => chatJid;

            public Task UpsertLiveMessagesAsync(
                string normalizedChatJid,
                IReadOnlyList<ChatMessage> messages)
            {
                if (ThrowOnUpsert)
                {
                    throw new System.InvalidOperationException("sqlite");
                }

                Upserted = true;
                UpsertList = new List<ChatMessage>(messages);
                return Task.CompletedTask;
            }

            public void LogLivePersistFailed(string message) => LoggedFail = true;
        }
    }
}
