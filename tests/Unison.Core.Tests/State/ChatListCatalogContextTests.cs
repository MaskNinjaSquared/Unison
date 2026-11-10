// =============================================================================
// ChatListCatalogContext — shared scope filter + canonical scratch.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public class ChatListCatalogContextTests
    {
        private sealed class InlineDispatcher : IDispatcher
        {
            public Task RunAsync(
                Action action,
                [CallerMemberName] string caller = null,
                [CallerFilePath] string callerFile = null)
            {
                action?.Invoke();
                return Task.CompletedTask;
            }
        }

        private static ChatStateStore NewStore() => new ChatStateStore(new InlineDispatcher());

        private static ChatListCatalogContext NewContext(
            ChatStateStore store,
            Dictionary<string, string> aliases = null)
        {
            aliases = aliases ?? new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            return new ChatListCatalogContext(
                store,
                jid =>
                {
                    string hit;
                    return aliases.TryGetValue(jid, out hit) ? hit : jid;
                },
                () => aliases.Count);
        }

        [Fact]
        public async Task ForScope_channels_returns_only_active_newsletters()
        {
            ChatStateStore store = NewStore();
            await store.UpsertChatsAsync(new[]
            {
                new ChatItem
                {
                    JID = "5511999990001@s.whatsapp.net",
                    Kind = ChatKind.Direct,
                    Status = ChatStatus.Active
                },
                new ChatItem
                {
                    JID = "120363@newsletter",
                    Kind = ChatKind.Channel,
                    Status = ChatStatus.Active
                },
                new ChatItem
                {
                    JID = "120364@newsletter",
                    Kind = ChatKind.Channel,
                    Status = ChatStatus.Archived
                }
            });

            ChatListCatalogContext context = NewContext(store);

            Assert.Equal(
                new[] { "120363@newsletter" },
                context.ForScope(ChatListScope.Channels).Select(c => c.JID));
            Assert.Equal(
                new[] { "5511999990001@s.whatsapp.net" },
                context.ForScope(ChatListScope.Active).Select(c => c.JID));
            Assert.Equal(
                new[] { "120364@newsletter" },
                context.ForScope(ChatListScope.Archived).Select(c => c.JID));
        }

        [Fact]
        public async Task Canonical_map_reuses_until_catalogue_or_alias_generation_changes()
        {
            ChatStateStore store = NewStore();
            await store.UpsertChatsAsync(new[]
            {
                new ChatItem { JID = "5511999990001@s.whatsapp.net", Status = ChatStatus.Active }
            });

            var aliases = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            ChatListCatalogContext context = NewContext(store, aliases);

            IReadOnlyDictionary<string, ChatItem> first = context.GetCanonicalMap();
            IReadOnlyDictionary<string, ChatItem> second = context.GetCanonicalMap();
            Assert.Same(first, second);

            aliases["5511999990001@s.whatsapp.net"] = "123@lid";
            IReadOnlyDictionary<string, ChatItem> afterAlias = context.GetCanonicalMap();
            Assert.NotSame(first, afterAlias);
            Assert.True(afterAlias.ContainsKey("123@lid"));
        }

        [Fact]
        public async Task ResolveByCanonical_finds_row_via_peek()
        {
            ChatStateStore store = NewStore();
            var row = new ChatItem { JID = "5511999990001@s.whatsapp.net", Status = ChatStatus.Active };
            await store.UpsertChatsAsync(new[] { row });

            var aliases = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                ["123@lid"] = "5511999990001@s.whatsapp.net"
            };
            ChatListCatalogContext context = NewContext(store, aliases);

            Assert.Same(row, context.ResolveByCanonical("123@lid"));
        }
    }
}
