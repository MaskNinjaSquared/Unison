// =============================================================================
// GroupsModule
//
// Every group operation, assembled on a session.
//
// Groups are nearly all request and response, and the changes other people make
// arrive as notifications the message layer already handles. This exists so a
// host takes one dependency instead of eight, and so the two pieces of wiring
// that matter are not forgotten: editing a description needs the group's
// current metadata, which is resolved here rather than asked of the caller, and
// the server's "your group list is stale" flag is answered here rather than
// left to a host that would otherwise poll for the same thing.
//
// Ports: rc14 makeGroupsSocket in src/Socket/groups.ts
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Events;
using Unison.Socket.Models;
using Unison.Socket.Session;
using Unison.Socket.UseCases.Chats;
using Unison.Socket.UseCases.Groups;

namespace Unison.Socket.Groups
{
    public sealed class GroupsModule : IDisposable
    {
        private readonly GroupMetadataProvider _cache;
        private readonly CleanDirtyBitsUseCase _clean;
        private readonly IWaEventBus _events;
        private readonly ISocketLog _log;
        private readonly IDisposable _dirtyRoute;

        /// <param name="cache">
        /// The send path's metadata cache. Passing the message layer's own keeps one copy of each
        /// group's participant list, and means a change made here is one the next message sees.
        /// </param>
        public GroupsModule(WhatsAppSession session, GroupMetadataProvider cache = null)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            var connection = session.Connection;

            Metadata = new FetchGroupMetadataUseCase(connection);
            Participating = new FetchParticipatingGroupsUseCase(connection);
            Create = new CreateGroupUseCase(connection);
            Participants = new ModifyGroupParticipantsUseCase(connection);
            Invites = new GroupInviteUseCase(connection);

            _cache = cache ?? new GroupMetadataProvider(Metadata, log: session.Log);

            Settings = new UpdateGroupSettingsUseCase(connection)
            {
                GetGroupMetadata = ResolveMetadataAsync
            };

            _clean = new CleanDirtyBitsUseCase(connection);
            _events = session.Events;
            _log = session.Log;
            _dirtyRoute = connection.Dispatcher.Register("ib,,dirty", OnDirtyAsync);
        }

        /// <summary>One group's details, including who is in it and who runs it.</summary>
        public FetchGroupMetadataUseCase Metadata { get; }

        /// <summary>Every group we belong to, in one query. Used on login.</summary>
        public FetchParticipatingGroupsUseCase Participating { get; }

        public CreateGroupUseCase Create { get; }

        /// <summary>Adding, removing, promoting, demoting, and join requests.</summary>
        public ModifyGroupParticipantsUseCase Participants { get; }

        /// <summary>Subject, description, permissions, disappearing timer, and leaving.</summary>
        public UpdateGroupSettingsUseCase Settings { get; }

        public GroupInviteUseCase Invites { get; }

        /// <summary>
        /// Drops a group from the send path's cache. Changes made through this module normally
        /// come back as a w:gp2 notification, which invalidates it already; this is for the host
        /// that cannot wait for the round trip.
        /// </summary>
        public void Invalidate(string groupJid)
        {
            _cache.Invalidate(groupJid);
        }

        public void Dispose()
        {
            if (_dirtyRoute != null)
            {
                _dirtyRoute.Dispose();
            }
        }

        /// <summary>
        /// The server's way of saying the group list moved while we were away. It sends no detail
        /// and expects the client to go and read, so we do that once and then clear the flag -
        /// leaving it set means being told the same thing on every connect.
        /// </summary>
        private async Task OnDirtyAsync(BinaryNode node)
        {
            var dirty = node.GetChild("dirty");
            if (dirty == null || dirty.GetAttribute("type") != "groups")
            {
                return;
            }

            _log.Info("[Groups] The server says our group list is stale, refreshing");

            var result = await Participating.ExecuteAsync().ConfigureAwait(false);
            if (result.FailureReason != null)
            {
                // The flag stays set on purpose. Clearing it after a failed read would tell the
                // server we caught up with something we never saw, and it will not offer again.
                _log.Warn("[Groups] Could not refresh the group list: " + result.FailureReason);
                return;
            }

            foreach (var group in result.Groups)
            {
                _cache.Set(group);
            }

            if (result.Groups.Count > 0)
            {
                await _events.EmitAsync(WaEventKind.GroupsUpdate, result.Groups).ConfigureAwait(false);
            }

            await _clean.ExecuteAsync("groups").ConfigureAwait(false);
        }

        private Task<GroupMetadata> ResolveMetadataAsync(string groupJid)
        {
            return _cache.GetAsync(groupJid);
        }
    }
}
