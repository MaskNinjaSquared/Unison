using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Models;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class PersistChatIdentityStateDispatchTests
    {
        [Fact]
        public async Task Empty_catalog_skips_persist()
        {
            var host = new FakeHost
            {
                Capture = new ChatIdentityCapture
                {
                    Catalog = ChatCatalogSnapshot.Take(new List<ChatItem>())
                }
            };
            await PersistChatIdentityStateDispatch.RunAsync(host, "t");
            Assert.False(host.CatalogPersisted);
        }

        [Fact]
        public async Task Persists_all_identity_slices()
        {
            var chat = new ChatItem { JID = "a@s.whatsapp.net", Name = "A" };
            var host = new FakeHost
            {
                Capture = new ChatIdentityCapture
                {
                    Catalog = ChatCatalogSnapshot.Take(new[] { chat }),
                    ContactNames = new Dictionary<string, string> { ["a@s.whatsapp.net"] = "A" },
                    PhoneNames = new Dictionary<string, string>(),
                    Aliases = new Dictionary<string, string>()
                }
            };
            await PersistChatIdentityStateDispatch.RunAsync(host, "go");
            Assert.True(host.CatalogPersisted);
            Assert.True(host.ContactsSaved);
            Assert.True(host.PhonesSaved);
            Assert.True(host.AliasesSaved);
        }

        private sealed class FakeHost : IPersistChatIdentityStateHost
        {
            public ChatIdentityCapture Capture;
            public bool CatalogPersisted;
            public bool ContactsSaved;
            public bool PhonesSaved;
            public bool AliasesSaved;

            public Task<ChatIdentityCapture> CaptureChatIdentitySnapshotsAsync() =>
                Task.FromResult(Capture);

            public Task PersistChatCatalogAsync(ChatCatalogSnapshot catalog)
            {
                CatalogPersisted = true;
                return Task.CompletedTask;
            }

            public Task SaveContactNamesAsync(
                IReadOnlyDictionary<string, string> names,
                IReadOnlyList<string> chatJids)
            {
                ContactsSaved = true;
                return Task.CompletedTask;
            }

            public Task SavePhoneContactNamesAsync(
                IReadOnlyDictionary<string, string> names,
                IReadOnlyList<string> chatJids)
            {
                PhonesSaved = true;
                return Task.CompletedTask;
            }

            public Task SaveJidAliasesAsync(
                IReadOnlyDictionary<string, string> aliases,
                IReadOnlyList<string> chatJids)
            {
                AliasesSaved = true;
                return Task.CompletedTask;
            }

            public void LogPersistChatIdentity(string message)
            {
            }
        }
    }
}
