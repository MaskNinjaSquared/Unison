using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;
using Unison.WhatsAppClient.Helpers;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ChatListAliasMergeTests
    {
        private const string Pn = "5511999990001@s.whatsapp.net";
        private const string Lid = "111@lid";

        private readonly JidAliasTable _aliases = new JidAliasTable();
        private readonly DisplayNameTable _pushNames = new DisplayNameTable();
        private readonly ChatStateStore _chats = new ChatStateStore(new CountingDispatcher());
        private readonly CountingDispatcher _ui = new CountingDispatcher();
        private readonly ChatRowIndex _rows;
        private readonly ChatListDeduper _deduper;

        public ChatListAliasMergeTests()
        {
            _rows = new ChatRowIndex(() => _chats.Chats, _aliases.GetCanonicalJid);
            _deduper = new ChatListDeduper(_ui, _chats, _rows, _aliases, _pushNames);
        }

        private ChatListAliasMerge Build() =>
            new ChatListAliasMerge(_ui, _chats, _rows, _aliases, _deduper, new NoMarkers());

        private sealed class NoMarkers : ISelfMarkerNaming
        {
            public bool IsMarkerLabel(string label) => false;

            public string StripMarker(string label) => label;
        }

        [Fact]
        public async Task Already_aliased_addresses_run_the_list_dedupe()
        {
            _aliases[Lid] = Pn;
            _aliases[Pn] = Lid;
            var lidRow = new ChatItem { JID = Lid };
            var pnRow = new ChatItem { JID = Pn };
            _chats.Chats.Add(lidRow);
            _chats.Chats.Add(pnRow);

            ChatListAliasMergeResult result = await Build().MergeAsync(Lid, Pn);

            Assert.Equal(ChatListAliasMergeOutcome.Deduped, result.Outcome);
            Assert.Single(_chats.Chats);
        }

        [Fact]
        public async Task Two_rows_without_an_alias_fold_messages_into_the_phone_row()
        {
            var lidRow = new ChatItem { JID = Lid };
            var pnRow = new ChatItem { JID = Pn };
            _chats.Chats.Add(lidRow);
            _chats.Chats.Add(pnRow);
            _chats.MessagesByChat.GetOrAdd(Lid).Add(new ChatMessage { Id = "L1" });
            _chats.MessagesByChat.GetOrAdd(Pn).Add(new ChatMessage { Id = "P1" });

            ChatListAliasMergeResult result = await Build().MergeAsync(Lid, Pn);

            Assert.Equal(ChatListAliasMergeOutcome.MergedRows, result.Outcome);
            Assert.DoesNotContain(lidRow, _chats.Chats);
            Assert.Contains(pnRow, _chats.Chats);
            Assert.False(_chats.MessagesByChat.Contains(Lid));
            Assert.Equal(2, _chats.MessagesByChat.CountFor(Pn));
        }

        [Fact]
        public async Task Usync_merge_keeps_the_newer_lid_preview_on_the_phone_row()
        {
            var lidRow = new ChatItem
            {
                JID = Lid,
                LastMessage = "from lid",
                LastMessageId = "L1",
                LastMessageTimestampUtc = new DateTime(2024, 6, 2, 12, 0, 0, DateTimeKind.Utc),
                UnreadCount = 3
            };
            var pnRow = new ChatItem
            {
                JID = Pn,
                LastMessage = "older pn",
                LastMessageId = "P1",
                LastMessageTimestampUtc = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc),
                UnreadCount = 1
            };
            _chats.Chats.Add(lidRow);
            _chats.Chats.Add(pnRow);

            ChatListAliasMergeResult result = await Build().MergeAsync(Lid, Pn);

            Assert.Equal(ChatListAliasMergeOutcome.MergedRows, result.Outcome);
            Assert.Equal("from lid", pnRow.LastMessage);
            Assert.Equal("L1", pnRow.LastMessageId);
            Assert.Equal(3, pnRow.UnreadCount);
        }

        [Fact]
        public async Task Missing_either_row_is_a_no_op()
        {
            _chats.Chats.Add(new ChatItem { JID = Pn });

            ChatListAliasMergeResult result = await Build().MergeAsync(Lid, Pn);

            Assert.Equal(ChatListAliasMergeOutcome.None, result.Outcome);
            Assert.Single(_chats.Chats);
        }

        [Fact]
        public async Task Blank_addresses_are_ignored()
        {
            Assert.Equal(
                ChatListAliasMergeOutcome.None,
                (await Build().MergeAsync(null, Pn)).Outcome);
        }
    }
}
