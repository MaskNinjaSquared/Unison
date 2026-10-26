using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ForgetChatStorageDispatchTests
    {
        [Fact]
        public void Merge_adds_canonical_when_missing()
        {
            List<string> jids = ForgetChatStorageKeys.Merge(
                "c@s.whatsapp.net",
                new[] { "a@lid" });

            Assert.Equal(new[] { "a@lid", "c@s.whatsapp.net" }, jids);
        }

        [Fact]
        public void Merge_does_not_duplicate_canonical()
        {
            List<string> jids = ForgetChatStorageKeys.Merge(
                "C@s.whatsapp.net",
                new[] { "c@s.whatsapp.net", "a@lid" });

            Assert.Equal(new[] { "c@s.whatsapp.net", "a@lid" }, jids);
        }

        [Fact]
        public async Task Run_tombstones_deletes_clears_then_persists()
        {
            var host = new FakeHost();

            await ForgetChatStorageDispatch.RunAsync(
                host,
                "c@s.whatsapp.net",
                new[] { "a@lid" });

            Assert.Equal(
                new[]
                {
                    "mark:a@lid,c@s.whatsapp.net",
                    "delete:a@lid,c@s.whatsapp.net",
                    "clear-cache",
                    "persist-catalog"
                },
                host.Log);
        }

        private sealed class FakeHost : IForgetChatStorageHost
        {
            public List<string> Log { get; } = new List<string>();

            public Task MarkPreviewsDeletedAsync(IReadOnlyList<string> jids)
            {
                Log.Add("mark:" + string.Join(",", jids));
                return Task.CompletedTask;
            }

            public Task DeleteHistoryForKeysAsync(IReadOnlyList<string> jids)
            {
                Log.Add("delete:" + string.Join(",", jids));
                return Task.CompletedTask;
            }

            public void ClearMessageMemoryCache() => Log.Add("clear-cache");

            public Task PersistCatalogAfterForgetAsync()
            {
                Log.Add("persist-catalog");
                return Task.CompletedTask;
            }
        }
    }
}