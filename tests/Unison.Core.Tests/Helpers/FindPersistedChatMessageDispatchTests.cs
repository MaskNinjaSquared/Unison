using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class FindPersistedChatMessageDispatchTests
    {
        [Fact]
        public async Task Returns_null_without_store()
        {
            var host = new FakeHost { HasStore = false };
            Assert.Null(await FindPersistedChatMessageDispatch.RunAsync(host, "a@x", "id1"));
        }

        [Fact]
        public async Task Scans_pin_keys_until_mapped()
        {
            var host = new FakeHost
            {
                HasStore = true,
                Rows = new Dictionary<string, object>
                {
                    { "b@x|id1", new object() }
                },
                MapResult = new ChatMessage { Id = "id1", Content = "hi" }
            };
            ChatMessage found = await FindPersistedChatMessageDispatch.RunAsync(host, "a@x", "id1");
            Assert.Equal("id1", found.Id);
            Assert.Contains("b@x", host.Lookups);
        }

        private sealed class FakeHost : IFindPersistedChatMessageHost
        {
            public bool HasStore;
            public Dictionary<string, object> Rows = new Dictionary<string, object>();
            public ChatMessage MapResult;
            public List<string> Lookups = new List<string>();

            public bool HasHistoryMessageStore => HasStore;

            public IJidResolver JidResolver => new FakeJids();

            public Task<object> GetHistoryMessageRowAsync(string storeKey, string messageId)
            {
                Lookups.Add(storeKey);
                Rows.TryGetValue(storeKey + "|" + messageId, out object row);
                return Task.FromResult(row);
            }

            public ChatMessage MapHistoryRowToChatMessage(object row) =>
                row == null ? null : MapResult;

            private sealed class FakeJids : IJidResolver
            {
                public string GetCanonicalJid(string jid) => "b@x";

                public bool TryGetAlias(string jid, out string alias)
                {
                    alias = null;
                    return false;
                }

                public bool IsSelfLinked(string jid) => false;

                public Unison.Core.Models.Profile Self => null;
            }
        }
    }
}
