using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;
using Unison.WhatsAppClient.Helpers;

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
        }

        [Fact]
        public async Task A_clean_list_costs_one_ui_hop()
        {
            PairLidWithPn();
            _chats.Chats.Add(new ChatItem { JID = Pn });
            _chats.Chats.Add(new ChatItem { JID = Other });
            _chats.MessagesByChat.GetOrAdd(Pn).Add(new ChatMessage { Id = "P1" });

            ChatListDeduperResult result = await Build().RunAsync("test");

            Assert.Equal(0, result.Merged);
            Assert.Equal(0, result.NormalizedKeys);
            Assert.Equal(1, _ui.Passes);
        }

        [Fact]
        public async Task Requests_during_a_pass_share_one_follow_up_pass()
        {
            _chats.Chats.Add(new ChatItem { JID = Pn });
            _chats.Chats.Add(new ChatItem { JID = Other });
            var deduper = Build();

            Task<ChatListDeduperResult> first = deduper.RunAsync("a");
            Task<ChatListDeduperResult> second = deduper.RunAsync("b");
            Task<ChatListDeduperResult> third = deduper.RunAsync("c");
            await Task.WhenAll(first, second, third);

            Assert.Same(second, third);
            // Follow-up sees the same alias generation and skips BuildPlan (no second UI hop).
            Assert.Equal(1, _ui.Passes);
            Assert.True((await second).SkippedUnchangedAliases);
        }

        [Fact]
        public async Task Second_pass_skips_when_alias_generation_unchanged()
        {
            _chats.Chats.Add(new ChatItem { JID = Pn });
            _chats.Chats.Add(new ChatItem { JID = Other });
            var deduper = Build();

            ChatListDeduperResult first = await deduper.RunAsync("first");
            ChatListDeduperResult second = await deduper.RunAsync("second");

            Assert.False(first.SkippedUnchangedAliases);
            Assert.True(second.SkippedUnchangedAliases);
            Assert.Equal(1, _ui.Passes);
        }

        [Fact]
        public async Task Alias_change_after_a_pass_forces_a_new_plan()
        {
            _chats.Chats.Add(new ChatItem { JID = Pn });
            var deduper = Build();

            await deduper.RunAsync("cold");
            int hopsAfterCold = _ui.Passes;
            PairLidWithPn();
            _chats.Chats.Add(new ChatItem { JID = Lid });

            ChatListDeduperResult result = await deduper.RunAsync("after-pair");

            Assert.False(result.SkippedUnchangedAliases);
            Assert.Equal(1, result.Merged);
            // Snapshot + one apply batch for the fold.
            Assert.Equal(hopsAfterCold + 2, _ui.Passes);
        }

        [Fact]
        public async Task A_row_readdressed_after_planning_is_not_folded()
        {
            PairLidWithPn();
            var lidRow = new ChatItem { JID = Lid };
            var pnRow = new ChatItem { JID = Pn };
            _chats.Chats.Add(lidRow);
            _chats.Chats.Add(pnRow);
            var ui = new HookedDispatcher();
            var deduper = new ChatListDeduper(
                ui,
                _chats,
                new ChatRowIndex(() => _chats.Chats, _aliases.GetCanonicalJid),
                _aliases,
                _pushNames);
            ui.BeforePass = pass =>
            {
                if (pass == 2)
                {
                    lidRow.JID = Other;
                }
            };

            ChatListDeduperResult result = await deduper.RunAsync("test");

            Assert.Equal(0, result.Merged);
            Assert.Equal(2, _chats.Chats.Count);
        }

        private sealed class HookedDispatcher : IDispatcher
        {
            private int _passes;

            public System.Action<int> BeforePass { get; set; }

            public Task RunAsync(System.Action action, string caller = null, string callerFile = null)
            {
                _passes++;
                BeforePass?.Invoke(_passes);
                action();
                return Task.CompletedTask;
            }
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
