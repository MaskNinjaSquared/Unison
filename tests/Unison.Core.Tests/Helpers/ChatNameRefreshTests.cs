using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;

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

        private ChatNameRefresh Build(IDispatcher ui = null)
        {
            _aliases.BindSelf(() => Me, () => null);
            var markers = new FakeMarkers();
            var names = new ContactDisplayNames(_aliases, new DisplayNameTable(), _addressBook, new RecordingPersonStore(), markers);
            names.Bind(() => new SelfNameInputs { Id = Me }, null);
            return new ChatNameRefresh(ui ?? _ui, _chats, names, markers);
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

        private sealed class FlagDispatcher : IDispatcher
        {
            public bool InPass { get; private set; }

            public Task RunAsync(System.Action action)
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

        private sealed class FakeMarkers : ISelfMarkerNaming
        {
            public bool IsMarkerLabel(string label) => label != null && label.Trim() == "(You)";

            public string StripMarker(string label) => IsMarkerLabel(label) ? null : label;
        }
    }
}
