using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ChatListDeduperTests
    {
        private const string Pn = "5511999990001@s.whatsapp.net";
        private const string Lid = "111@lid";
        private const string Other = "5511999990002@s.whatsapp.net";

        private readonly JidAliasTable _aliases = new JidAliasTable();
        private readonly DisplayNameTable _pushNames = new DisplayNameTable();
        private readonly ChatStateStore _chats = new ChatStateStore(new CountingDispatcher());
        private readonly CountingDispatcher _ui = new CountingDispatcher();
        private ChatRowIndex _rows;

        private ChatListDeduper Build()
        {
            _rows = new ChatRowIndex(() => _chats.Chats, _aliases.GetCanonicalJid);
            return new ChatListDeduper(_ui, _chats, _rows, _aliases, _pushNames);
        }

        private void PairLidWithPn()
        {
            _aliases[Lid] = Pn;
            _aliases[Pn] = Lid;
        }

        [Fact]
        public async Task A_lone_row_is_rewritten_to_its_canonical_address()
        {
            PairLidWithPn();
            var chat = new ChatItem { JID = Lid };
            _chats.Chats.Add(chat);

            ChatListDeduperResult result = await Build().RunAsync("test");

            Assert.Equal(Pn, chat.JID);
            Assert.Equal(0, result.Merged);
            Assert.Equal(1, _ui.Passes);
        }

        [Fact]
        public async Task Duplicate_lid_and_phone_rows_collapse_to_one()
        {
            PairLidWithPn();
            var lidRow = new ChatItem { JID = Lid };
            var pnRow = new ChatItem { JID = Pn };
            _chats.Chats.Add(lidRow);
            _chats.Chats.Add(pnRow);
            _chats.MessagesByChat.GetOrAdd(Lid).Add(new ChatMessage { Id = "L1" });
            _chats.MessagesByChat.GetOrAdd(Pn).Add(new ChatMessage { Id = "P1" });

            ChatListDeduperResult result = await Build().RunAsync("test");

            Assert.Equal(1, result.Merged);
            Assert.Single(_chats.Chats);
            Assert.DoesNotContain(lidRow, _chats.Chats);
            Assert.False(_chats.MessagesByChat.Contains(Lid));
            Assert.Equal(2, _chats.MessagesByChat.CountFor(Pn));
        }

        [Fact]
        public async Task Distinct_people_are_left_alone()
        {
            _chats.Chats.Add(new ChatItem { JID = Pn });
            _chats.Chats.Add(new ChatItem { JID = Other });

            ChatListDeduperResult result = await Build().RunAsync("test");

            Assert.Equal(0, result.Merged);
            Assert.Equal(2, _chats.Chats.Count);
        }

        [Fact]
        public async Task Message_cache_keys_under_an_alias_fold_into_the_canonical()
        {
            PairLidWithPn();
            _chats.Chats.Add(new ChatItem { JID = Pn });
            _chats.MessagesByChat.GetOrAdd(Lid).Add(new ChatMessage { Id = "L1" });
            _chats.MessagesByChat.GetOrAdd(Pn).Add(new ChatMessage { Id = "P1" });

            ChatListDeduperResult result = await Build().RunAsync("test");

            Assert.True(result.NormalizedKeys >= 1);
            Assert.False(_chats.MessagesByChat.Contains(Lid));
            Assert.Equal(2, _chats.MessagesByChat.CountFor(Pn));
        }

        [Fact]
        public async Task Applied_is_raised_only_when_something_changed()
        {
            _chats.Chats.Add(new ChatItem { JID = Pn });
            var deduper = Build();
            var heard = new List<ChatListDeduperResult>();
            deduper.Applied += (s, r) => heard.Add(r);

            await deduper.RunAsync("noop");
            Assert.Empty(heard);

            PairLidWithPn();
            _chats.Chats.Add(new ChatItem { JID = Lid });
            await deduper.RunAsync("merge");

            Assert.Single(heard);
            Assert.Equal(1, heard[0].Merged);
        }
    }
}
