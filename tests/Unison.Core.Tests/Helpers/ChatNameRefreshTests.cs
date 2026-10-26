using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;
using Unison.WhatsAppClient.Helpers;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ChatNameRefreshTests
    {
        private const string Ana = "5511988887777@s.whatsapp.net";
        private const string Me = "5511911112222@s.whatsapp.net";

        private readonly JidAliasTable _aliases = new JidAliasTable();
        private readonly DisplayNameTable _addressBook = new DisplayNameTable();
        private readonly ChatStateStore _chats = new ChatStateStore(new CountingDispatcher());
        private readonly CountingDispatcher _ui = new CountingDispatcher();

        private ChatNameRefresh Build(IDispatcher ui = null, bool preferFrugal = false)
        {
            _aliases.BindSelf(() => Me, () => null);
            var markers = new FakeMarkers();
            var names = new ContactDisplayNames(_aliases, new DisplayNameTable(), _addressBook, new RecordingPersonStore(), markers);
            names.Bind(() => new SelfNameInputs { Id = Me }, null);
            return new ChatNameRefresh(
                ui ?? _ui,
                _chats,
                names,
                markers,
                new FakeGroupNaming(),
                () => preferFrugal);
        }

        [Fact]
        public async Task A_group_row_takes_the_subject_from_group_naming()
        {
            const string group = "120363000000000001@g.us";
            var chat = new ChatItem { JID = group, Name = "120363000000000001", IsGroup = true };
            _chats.Chats.Add(chat);

            var markers = new FakeMarkers();
            var names = new ContactDisplayNames(
                _aliases,
                new DisplayNameTable(),
                _addressBook,
                new RecordingPersonStore(),
                markers);
            names.Bind(() => new SelfNameInputs { Id = Me }, null);
            var groupNaming = new FakeGroupNaming();
            groupNaming.ChatNames[group] = "Família Nova";
            var refresh = new ChatNameRefresh(_ui, _chats, names, markers, groupNaming);

            await refresh.ApplyAsync();

            Assert.Equal("Família Nova", chat.Name);
        }

        [Fact]
        public async Task A_row_named_by_its_number_takes_the_known_name_in_one_pass()
        {
            var chat = new ChatItem { JID = Ana, Name = "5511988887777" };
            _chats.Chats.Add(chat);
            _addressBook.Set(Ana, "Ana Trabalho");
            var refresh = Build();
            var applied = new List<int>();
            refresh.Applied += (s, count) => applied.Add(count);

            await refresh.ApplyAsync();

            Assert.Equal("Ana Trabalho", chat.Name);
            Assert.Equal(1, _ui.Passes);
            Assert.Equal(new[] { 1 }, applied);
        }

        [Fact]
        public async Task Nothing_is_announced_when_no_row_changed()
        {
            _chats.Chats.Add(new ChatItem { JID = Ana, Name = "Ana Trabalho" });
            _addressBook.Set(Ana, "Ana Trabalho");
            var refresh = Build();
            bool raised = false;
            refresh.Applied += (s, count) => raised = true;

            await refresh.ApplyAsync();

            Assert.False(raised);
        }

        [Fact]
        public async Task The_announcement_happens_inside_the_ui_pass()
        {
            _chats.Chats.Add(new ChatItem { JID = Ana, Name = "5511988887777" });
            _addressBook.Set(Ana, "Ana Trabalho");
            var ui = new FlagDispatcher();
            var refresh = Build(ui);
            bool raisedInPass = false;
            refresh.Applied += (s, count) => raisedInPass = ui.InPass;

            await refresh.ApplyAsync();

            Assert.True(raisedInPass);
        }

        [Fact]
        public async Task Frugal_apply_slices_across_ui_passes_and_announces_once()
        {
            int total = ChatNameRefresh.FrugalUiSliceSize + 5;
            for (int i = 0; i < total; i++)
            {
                string jid = "551190000" + i.ToString("D4") + "@s.whatsapp.net";
                _chats.Chats.Add(new ChatItem { JID = jid, Name = "551190000" + i.ToString("D4") });
                _addressBook.Set(jid, "Nome " + i);
            }

            var refresh = Build(preferFrugal: true);
            var applied = new List<int>();
            refresh.Applied += (s, count) => applied.Add(count);

            await refresh.ApplyAsync();

            Assert.Equal(2, _ui.Passes);
            Assert.Equal(new[] { total }, applied);
            Assert.Equal("Nome 0", _chats.Chats[0].Name);
            Assert.Equal("Nome " + (total - 1), _chats.Chats[total - 1].Name);
        }

        [Fact]
        public async Task Concurrent_applies_coalesce_into_one_ui_walk()
        {
            _chats.Chats.Add(new ChatItem { JID = Ana, Name = "5511988887777" });
            _addressBook.Set(Ana, "Ana Trabalho");

            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var ui = new GatedDispatcher(entered, gate);
            var refresh = Build(ui);

            Task first = refresh.ApplyAsync();
            await entered.Task;

            Task second = refresh.ApplyAsync();
            Task third = refresh.ApplyAsync();
            gate.TrySetResult(true);

            await Task.WhenAll(first, second, third);

            // In-flight walk + at most one follow-up for tickets that arrived mid-pass —
            // never one catalogue walk per caller (would be 3 here).
            Assert.InRange(ui.Passes, 1, 2);
            Assert.Equal("Ana Trabalho", _chats.Chats[0].Name);
        }

        private sealed class FlagDispatcher : IDispatcher
        {
            public bool InPass { get; private set; }

            public Task RunAsync(System.Action action, string caller = null, string callerFile = null)
            {
                InPass = true;
                try
                {
                    action();
                }
                finally
                {
                    InPass = false;
                }

                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Blocks inside the first RunAsync until <paramref name="release"/> completes so
        /// overlapping ApplyAsync callers can queue tickets against a held gate.
        /// </summary>
        private sealed class GatedDispatcher : IDispatcher
        {
            private readonly TaskCompletionSource<bool> _entered;
            private readonly TaskCompletionSource<bool> _release;
            private int _passes;

            public GatedDispatcher(
                TaskCompletionSource<bool> entered,
                TaskCompletionSource<bool> release)
            {
                _entered = entered;
                _release = release;
            }

            public int Passes => _passes;

            public async Task RunAsync(System.Action action, string caller = null, string callerFile = null)
            {
                int pass = Interlocked.Increment(ref _passes);
                if (pass == 1)
                {
                    _entered.TrySetResult(true);
                    await _release.Task.ConfigureAwait(false);
                }

                action();
            }
        }

        private sealed class FakeMarkers : ISelfMarkerNaming
        {
            public bool IsMarkerLabel(string label) => label != null && label.Trim() == "(You)";

            public string StripMarker(string label) => IsMarkerLabel(label) ? null : label;
        }
    }
}
