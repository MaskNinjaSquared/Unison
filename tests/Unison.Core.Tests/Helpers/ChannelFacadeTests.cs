// =============================================================================
// ChannelFacade — create a channel (then its picture) and keep this account's
// role on the row so owners and admins get the composer.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Channels;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ChannelFacadeTests
    {
        private const string NewChannel = "120363000000000010@newsletter";

        [Fact]
        public async Task Channel_is_created_then_gets_its_picture()
        {
            var wire = new FakeWire { Created = new ChannelCreatedSnapshot { Jid = NewChannel, ViewerRole = "OWNER" } };
            var facade = NewFacade(wire, encoder: new FakeEncoder(new byte[] { 0xFF, 0xD8 }));

            ChannelCreateResult result = await facade.CreateChannelAsync(new ChannelCreateRequest
            {
                Name = "  Unison News ",
                Description = " Updates ",
                PhotoBytes = new byte[] { 1, 2, 3 }
            });

            Assert.True(result.Succeeded);
            Assert.False(result.PhotoFailed);
            Assert.Equal(NewChannel, result.ChannelJid);
            Assert.Equal("Unison News", wire.Name);
            Assert.Equal("Updates", wire.Description);
            Assert.Equal(new[] { "picture:" + NewChannel }, wire.FollowUps);
        }

        [Fact]
        public async Task Empty_name_is_refused_before_the_wire()
        {
            var wire = new FakeWire();
            var facade = NewFacade(wire);

            ChannelCreateResult result = await facade.CreateChannelAsync(new ChannelCreateRequest { Name = "  " });

            Assert.Equal(ChannelCreateResult.ReasonEmptyName, result.FailureReason);
            Assert.Null(wire.Name);
        }

        [Fact]
        public async Task Offline_create_is_refused_before_the_wire()
        {
            var wire = new FakeWire { Connected = false };
            var facade = NewFacade(wire);

            ChannelCreateResult result = await facade.CreateChannelAsync(new ChannelCreateRequest { Name = "News" });

            Assert.Equal(ChannelCreateResult.ReasonNotConnected, result.FailureReason);
            Assert.Null(wire.Name);
        }

        [Fact]
        public async Task Refused_create_reports_the_server_reason()
        {
            var wire = new FakeWire { CreateError = new InvalidOperationException("403") };
            var facade = NewFacade(wire);

            ChannelCreateResult result = await facade.CreateChannelAsync(new ChannelCreateRequest { Name = "News" });

            Assert.False(result.Succeeded);
            Assert.Equal("403", result.FailureReason);
        }

        [Fact]
        public async Task Picture_failure_keeps_the_channel()
        {
            var wire = new FakeWire
            {
                Created = new ChannelCreatedSnapshot { Jid = NewChannel },
                PictureError = new InvalidOperationException("refused")
            };
            var facade = NewFacade(wire, encoder: new FakeEncoder(new byte[] { 0xFF }));

            ChannelCreateResult result = await facade.CreateChannelAsync(new ChannelCreateRequest
            {
                Name = "News",
                PhotoBytes = new byte[] { 1 }
            });

            Assert.True(result.Succeeded);
            Assert.True(result.PhotoFailed);
        }

        [Fact]
        public async Task Created_channel_is_stored_as_owned()
        {
            var wire = new FakeWire { Created = new ChannelCreatedSnapshot { Jid = NewChannel } };
            var store = new FakeChatStore();

            await NewFacade(wire, store).CreateChannelAsync(new ChannelCreateRequest { Name = "News" });

            Assert.Equal(ChannelViewerRole.Owner, store.TryGetCached(NewChannel).ChannelRole);
        }

        [Fact]
        public async Task Stored_role_unlocks_the_composer_after_a_restart_while_offline()
        {
            var store = new FakeChatStore();
            await store.SetChannelRoleAsync(NewChannel, ChannelViewerRole.Owner);
            var wire = new FakeWire { Connected = false };
            var row = new ChatItem { JID = NewChannel, Kind = ChatKind.Channel };

            await NewFacade(wire, store).RefreshViewerRoleAsync(row);

            Assert.Equal(ChannelViewerRole.Owner, row.ChannelRole);
            Assert.False(row.IsComposerLocked);
        }

        [Fact]
        public async Task Refresh_writes_only_when_the_server_role_differs()
        {
            var store = new FakeChatStore();
            await store.SetChannelRoleAsync(NewChannel, ChannelViewerRole.Admin);
            store.RoleWrites = 0;
            var wire = new FakeWire { FetchedRole = "ADMIN" };
            var facade = NewFacade(wire, store);
            var row = new ChatItem { JID = NewChannel, Kind = ChatKind.Channel };

            await facade.RefreshViewerRoleAsync(row);
            Assert.Equal(0, store.RoleWrites);

            wire.FetchedRole = "SUBSCRIBER";
            await facade.RefreshViewerRoleAsync(row);

            Assert.Equal(1, store.RoleWrites);
            Assert.Equal(ChannelViewerRole.Subscriber, store.TryGetCached(NewChannel).ChannelRole);
            Assert.True(row.IsComposerLocked);
        }

        [Fact]
        public async Task Refresh_applies_the_role_the_server_reports()
        {
            var wire = new FakeWire { FetchedRole = "ADMIN" };
            var facade = NewFacade(wire);
            var row = new ChatItem { JID = NewChannel, Kind = ChatKind.Channel };

            await facade.RefreshViewerRoleAsync(row);

            Assert.Equal(ChannelViewerRole.Admin, row.ChannelRole);
        }

        [Fact]
        public async Task Failed_fetch_keeps_the_last_known_role()
        {
            var wire = new FakeWire { FetchedRole = "ADMIN" };
            var facade = NewFacade(wire);
            var row = new ChatItem { JID = NewChannel, Kind = ChatKind.Channel };
            await facade.RefreshViewerRoleAsync(row);
            wire.FetchedRole = null;

            await facade.RefreshViewerRoleAsync(row);

            Assert.Equal(ChannelViewerRole.Admin, row.ChannelRole);
        }

        [Fact]
        public async Task Refresh_ignores_rows_that_are_not_channels()
        {
            var wire = new FakeWire { FetchedRole = "OWNER" };
            var facade = NewFacade(wire);
            var row = new ChatItem { JID = "120363000000000003@g.us", Kind = ChatKind.Group };

            await facade.RefreshViewerRoleAsync(row);

            Assert.Equal(0, wire.Fetches);
            Assert.Equal(ChannelViewerRole.Unknown, row.ChannelRole);
        }

        private static ChannelFacade NewFacade(
            FakeWire wire,
            FakeChatStore store = null,
            IProfilePictureEncoder encoder = null)
        {
            return new ChannelFacade(wire, new CountingDispatcher(), store ?? new FakeChatStore(), encoder);
        }

        private sealed class FakeChatStore : IChatStore
        {
            private readonly ChatLocalStateCache _cache = new ChatLocalStateCache();

            public int RoleWrites { get; set; }

            public Task SetChannelRoleAsync(string jid, ChannelViewerRole role)
            {
                RoleWrites++;
                _cache.Mutate(jid, state => state.ChannelRole = role);
                return Task.CompletedTask;
            }

            public ChatLocalState TryGetCached(string jid) => _cache.TryGet(jid);

            public Task<ChatLocalState> GetAsync(string jid) => Task.FromResult(_cache.TryGet(jid));

            public Task InitializeAsync() => Task.CompletedTask;

            public Task WarmAsync() => Task.CompletedTask;

            public IReadOnlyList<string> ListFavoriteJids() => _cache.ListFavoriteJids();

            public Task SetChatPinnedAsync(string jid, bool pinned, long? pinnedTimestamp = null) => Task.CompletedTask;

            public Task SetWidgetPinnedAsync(string jid, bool pinned) => Task.CompletedTask;

            public Task SetFavoriteAsync(string jid, bool favorite) => Task.CompletedTask;

            public Task SetMutedUntilAsync(string jid, long? mutedUntil) => Task.CompletedTask;

            public Task SetStatusAsync(string jid, ChatStatus status) => Task.CompletedTask;

            public void RememberChatPinned(string jid, bool pinned, long? pinnedTimestamp = null)
            {
            }

            public void RememberFavorite(string jid, bool favorite)
            {
            }

            public void RememberMutedUntil(string jid, long? mutedUntil)
            {
            }

            public void ApplyTo(ChatItem chat)
            {
            }

            public Task ApplyToAsync(ChatItem chat) => Task.CompletedTask;
        }

        private sealed class FakeEncoder : IProfilePictureEncoder
        {
            private readonly byte[] _output;

            public FakeEncoder(byte[] output)
            {
                _output = output;
            }

            public Task<byte[]> EncodeSquareJpegAsync(byte[] image) => Task.FromResult(_output);
        }

        private sealed class FakeWire : IChannelWireActions
        {
            public bool Connected { get; set; } = true;

            public ChannelCreatedSnapshot Created { get; set; }

            public Exception CreateError { get; set; }

            public Exception PictureError { get; set; }

            public string FetchedRole { get; set; }

            public string Name { get; private set; }

            public string Description { get; private set; }

            public int Fetches { get; private set; }

            public List<string> FollowUps { get; } = new List<string>();

            public bool IsHandshakeComplete => Connected;

            public Task<ChannelCreatedSnapshot> CreateChannelAsync(string name, string description)
            {
                if (CreateError != null)
                {
                    return Task.FromException<ChannelCreatedSnapshot>(CreateError);
                }

                Name = name;
                Description = description;
                return Task.FromResult(Created);
            }

            public Task UpdateChannelPictureAsync(string channelJid, byte[] jpeg)
            {
                if (PictureError != null)
                {
                    return Task.FromException(PictureError);
                }

                FollowUps.Add("picture:" + channelJid);
                return Task.CompletedTask;
            }

            public Task<string> FetchViewerRoleAsync(string channelJid)
            {
                Fetches++;
                return Task.FromResult(FetchedRole);
            }
        }
    }
}
