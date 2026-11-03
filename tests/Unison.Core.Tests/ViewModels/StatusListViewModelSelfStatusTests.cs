using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.Tests.Helpers;
using Unison.Core.ViewModels;
using Xunit;

namespace Unison.Core.Tests.ViewModels
{
    public sealed class StatusListViewModelSelfStatusTests
    {
        private const string SelfJid = "5511999990000@s.whatsapp.net";
        private const string SelfLid = "274401232732233@lid";
        private const string OtherJid = "5511888880000@s.whatsapp.net";

        [Fact]
        public async Task Own_author_moves_to_SelfStatus_titled_my_status()
        {
            var vm = CreateViewModel();

            await vm.ReloadAsync();

            Assert.Single(vm.Authors);
            Assert.Equal(OtherJid, vm.Authors[0].Jid);
            Assert.True(vm.HasSelfStatus);
            Assert.Equal(SelfJid, vm.SelfStatus.Jid);
            Assert.Equal("My status", vm.SelfStatus.DisplayName);
            Assert.Equal("3 updates", vm.SelfStatus.ViewStateCaption);
        }

        [Fact]
        public async Task Own_lid_author_is_recognised_as_self()
        {
            var status = new FakeStatus(new[]
            {
                new StatusAuthorItem { Jid = SelfLid, ItemCount = 2 },
                new StatusAuthorItem { Jid = OtherJid, ItemCount = 1 }
            });
            var vm = new StatusListViewModel(status, new CountingDispatcher(), jids: new SelfJids(SelfJid, SelfLid));

            await vm.ReloadAsync();

            Assert.Single(vm.Authors);
            Assert.Equal(SelfLid, vm.SelfStatus?.Jid);
        }

        [Fact]
        public async Task Without_own_status_there_is_no_SelfStatus()
        {
            var status = new FakeStatus(new[]
            {
                new StatusAuthorItem { Jid = OtherJid, ItemCount = 1 }
            });
            var vm = new StatusListViewModel(status, new CountingDispatcher(), jids: new SelfJids(SelfJid));

            await vm.ReloadAsync();

            Assert.Single(vm.Authors);
            Assert.False(vm.HasSelfStatus);
        }

        [Fact]
        public async Task Selected_self_status_survives_reload()
        {
            var vm = CreateViewModel();
            await vm.ReloadAsync();
            bool cleared = false;
            vm.SelectionCleared += (s, e) => cleared = true;

            vm.SelectedAuthor = vm.SelfStatus;
            await vm.ReloadAsync();

            Assert.False(cleared);
            Assert.Same(vm.SelfStatus, vm.SelectedAuthor);
        }

        [Fact]
        public async Task OpenMyStatus_loads_own_items_with_view_count_text()
        {
            var status = new FakeStatus(new[]
            {
                new StatusAuthorItem { Jid = SelfJid, ItemCount = 2 }
            })
            {
                OwnStatuses = new[]
                {
                    new OwnStatusItem { MessageId = "A", ViewCount = 1 },
                    new OwnStatusItem { MessageId = "B", ViewCount = 4 }
                }
            };
            var vm = new StatusListViewModel(status, new CountingDispatcher(), jids: new SelfJids(SelfJid));
            await vm.ReloadAsync();

            await vm.OpenMyStatusAsync();

            Assert.True(vm.IsMyStatusOpen);
            Assert.Equal(SelfJid, status.OwnStatusesRequestedFor);
            Assert.Equal(2, vm.OwnStatuses.Count);
            Assert.Equal("1 view", vm.OwnStatuses[0].ViewCountText);
            Assert.Equal("4 views", vm.OwnStatuses[1].ViewCountText);
        }

        [Fact]
        public async Task CloseMyStatus_hides_the_page()
        {
            var vm = CreateViewModel();
            await vm.ReloadAsync();
            await vm.OpenMyStatusAsync();

            vm.CloseMyStatus();

            Assert.False(vm.IsMyStatusOpen);
        }

        private static StatusListViewModel CreateViewModel()
        {
            var status = new FakeStatus(new[]
            {
                new StatusAuthorItem { Jid = OtherJid, DisplayName = "Ana", ItemCount = 1 },
                new StatusAuthorItem { Jid = SelfJid, DisplayName = "Eu", ItemCount = 3, AvatarUrl = "ms-appdata:///local/thumb.jpg" }
            });
            return new StatusListViewModel(status, new CountingDispatcher(), jids: new SelfJids(SelfJid));
        }

        private sealed class FakeStatus : IStatusService
        {
            private readonly IReadOnlyList<StatusAuthorItem> _authors;

            public FakeStatus(IReadOnlyList<StatusAuthorItem> authors)
            {
                _authors = authors;
            }

            public event EventHandler StatusUpdated { add { } remove { } }

            public Task<IReadOnlyList<StatusAuthorItem>> GetActiveAuthorsAsync() => Task.FromResult(_authors);

            public Task<IReadOnlyList<HistoryStatus>> GetActiveForAuthorAsync(string authorJid) =>
                Task.FromResult<IReadOnlyList<HistoryStatus>>(Array.Empty<HistoryStatus>());

            public Task<string> EnsureMediaAsync(HistoryStatus status) => Task.FromResult<string>(null);

            public Task MarkViewedAsync(string authorJid, string messageId) => Task.CompletedTask;

            public Task RecordViewersAsync(IEnumerable<string> messageIds, string viewerJid, DateTime viewedAtUtc) =>
                Task.CompletedTask;

            public IReadOnlyList<OwnStatusItem> OwnStatuses { get; set; } = Array.Empty<OwnStatusItem>();

            public string OwnStatusesRequestedFor { get; private set; }

            public Task<IReadOnlyList<OwnStatusItem>> GetOwnStatusesAsync(string selfJid)
            {
                OwnStatusesRequestedFor = selfJid;
                return Task.FromResult(OwnStatuses);
            }

            public string ResolveMediaDisplayUri(string downloadedUri, HistoryStatus item) => downloadedUri;

            public bool ShowStatusAsVideo(ChatPreviewKind kind, string displayUri) => false;

            public bool ShowStatusAsImage(ChatPreviewKind kind, string displayUri) => false;

            public string DescribeStatusItem(HistoryStatus item, int index = -1) => string.Empty;

            public Task IngestLiveFromDecryptedAsync(IncomingLiveStatusRequest request) => Task.CompletedTask;
        }

        private sealed class SelfJids : IJidResolver
        {
            private readonly string[] _selves;

            public SelfJids(params string[] selves)
            {
                _selves = selves;
            }

            public Profile Self => null;

            public string GetCanonicalJid(string jid) => JidHelper.Normalize(jid) ?? jid;

            public bool TryGetAlias(string jid, out string alias)
            {
                alias = null;
                return false;
            }

            public bool IsSelfLinked(string jid) =>
                Array.Exists(_selves, self => string.Equals(
                    JidHelper.Normalize(jid),
                    JidHelper.Normalize(self),
                    StringComparison.OrdinalIgnoreCase));
        }
    }
}
