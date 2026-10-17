using System;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class StoredChatPreviewRefreshTests
    {
        private const string Ana = "5511988887777@s.whatsapp.net";
        private const string Group = "120363000000000001@g.us";
        private const string Me = "5511911112222@s.whatsapp.net";
        private static readonly DateTime At = new DateTime(2026, 3, 1, 15, 0, 0, DateTimeKind.Utc);

        private readonly JidAliasTable _aliases = new JidAliasTable();
        private readonly DisplayNameTable _addressBook = new DisplayNameTable();
        private readonly ChatStateStore _chats = new ChatStateStore(new CountingDispatcher());
        private readonly CountingDispatcher _ui = new CountingDispatcher();

        private StoredChatPreviewRefresh Build()
        {
            _aliases.BindSelf(() => Me, () => null);
            var markers = new FakeMarkers();
            var names = new ContactDisplayNames(
                _aliases, new DisplayNameTable(), _addressBook, new RecordingPersonStore(), markers);
            names.Bind(() => new SelfNameInputs { Id = Me }, null);
            return new StoredChatPreviewRefresh(
                _ui,
                _chats,
                new AliasJids(_aliases),
                names,
                markers,
                new FakeStrings(),
                () => At.AddHours(1));
        }

        [Fact]
        public async Task Refresh_puts_the_newest_stored_tip_on_each_row()
        {
            var chat = new ChatItem { JID = Ana, Name = "Ana" };
            _chats.Chats.Add(chat);
            await _chats.UpsertMessagesAsync(Ana, new[]
            {
                Msg("m1", "velho", At.AddMinutes(-10)),
                Msg("m2", "novo", At)
            });

            int updated = await Build().RefreshAsync("test");

            Assert.Equal(1, updated);
            Assert.Equal(1, _ui.Passes);
            Assert.Equal("novo", chat.LastMessage);
            Assert.Equal("m2", chat.LastMessageId);
        }

        [Fact]
        public async Task Refresh_skips_a_row_with_no_usable_tip()
        {
            var chat = new ChatItem { JID = Ana, Name = "Ana", LastMessage = "keep" };
            _chats.Chats.Add(chat);
            await _chats.UpsertMessagesAsync(Ana, new[]
            {
                Msg("bad", "x", DateTime.MinValue)
            });

            int updated = await Build().RefreshAsync("test");

            Assert.Equal(0, updated);
            Assert.Equal("keep", chat.LastMessage);
        }

        [Fact]
        public async Task Reconcile_creates_a_row_for_a_timeline_the_list_lacks()
        {
            _addressBook.Set(Ana, "Ana Trabalho");
            await _chats.UpsertMessagesAsync(Ana, new[] { Msg("m1", "oi", At) });

            var result = await Build().ReconcileAsync("test");

            Assert.Equal(1, result.Refreshed);
            Assert.Equal(1, result.Created);
            Assert.Single(_chats.Chats);
            ChatItem chat = _chats.Chats[0];
            Assert.Equal(Ana, chat.JID);
            Assert.Equal("Ana Trabalho", chat.Name);
            Assert.Equal("oi", chat.LastMessage);
            Assert.Equal(ChatKind.Direct, chat.Kind);
        }

        [Fact]
        public async Task Reconcile_renames_a_placeholder_direct_row()
        {
            var chat = new ChatItem { JID = Ana, Name = "5511988887777", Kind = ChatKind.Direct };
            _chats.Chats.Add(chat);
            _addressBook.Set(Ana, "Ana Trabalho");
            await _chats.UpsertMessagesAsync(Ana, new[] { Msg("m1", "oi", At) });

            await Build().ReconcileAsync("test");

            Assert.Equal("Ana Trabalho", chat.Name);
            Assert.Equal("oi", chat.LastMessage);
        }

        [Fact]
        public async Task Reconcile_sorts_the_list_after_applying_tips()
        {
            var older = new ChatItem { JID = Ana, Name = "Ana" };
            var newer = new ChatItem { JID = Group, Name = "Família", IsGroup = true };
            _chats.Chats.Add(older);
            _chats.Chats.Add(newer);
            await _chats.UpsertMessagesAsync(Ana, new[] { Msg("a", "a", At.AddHours(-2)) });
            await _chats.UpsertMessagesAsync(Group, new[] { Msg("g", "g", At) });

            await Build().ReconcileAsync("test");

            Assert.Equal(Group, _chats.Chats[0].JID);
            Assert.Equal(Ana, _chats.Chats[1].JID);
        }

        private static ChatMessage Msg(string id, string content, DateTime at) =>
            new ChatMessage
            {
                Id = id,
                Content = content,
                Timestamp = at,
                IsFromMe = false,
                Status = ChatMessage.StatusDelivered
            };

        private sealed class FakeMarkers : ISelfMarkerNaming
        {
            public bool IsMarkerLabel(string label) => label != null && label.Trim() == "(You)";

            public string StripMarker(string label) => IsMarkerLabel(label) ? null : label;
        }

        private sealed class FakeStrings : IStringResources
        {
            public string Get(string key, string fallback = null)
            {
                if (key == "Chat_SelfFallbackName")
                {
                    return "Você";
                }

                if (key == "Common_Yesterday")
                {
                    return "Ontem";
                }

                return fallback;
            }
        }

        private sealed class AliasJids : IJidResolver
        {
            private readonly JidAliasTable _aliases;

            public AliasJids(JidAliasTable aliases) => _aliases = aliases;

            public Profile Self => null;

            public string GetCanonicalJid(string jid) => _aliases.GetCanonicalJid(jid);

            public bool TryGetAlias(string jid, out string alias)
            {
                alias = null;
                return false;
            }

            public bool IsSelfLinked(string jid) => _aliases.IsSelfLinked(jid);
        }
    }
}
