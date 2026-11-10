// =============================================================================
// BridgeGroupWireActions
//
// Forwards IGroupWireActions to the live IWhatsAppSocket from the bridge.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Core.Models;
using Unison.Uwp.Client;
using Unison.WhatsAppClient.Abstractions;
using UwpSessionProvider = Unison.Uwp.Services.Socket.IWhatsAppSessionProvider;

namespace Unison.Uwp.Services.WhatsApp.Groups
{
    /// <summary>
    /// Internal: constructor takes the UWP bridge provider (<c>internal</c>), so this type
    /// cannot be public without widening that interface.
    /// </summary>
    internal sealed class BridgeGroupWireActions : IGroupWireActions
    {
        private readonly UwpSessionProvider _sessions;

        public BridgeGroupWireActions(UwpSessionProvider sessions)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        }

        public bool IsHandshakeComplete
        {
            get
            {
                IWhatsAppSocket socket = _sessions.Socket;
                return socket != null && socket.IsHandshakeComplete;
            }
        }

        public Task<BinaryNode> QueryGroupMetadataAsync(
            string groupJid,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            IWhatsAppSocket socket = _sessions.Socket;
            if (socket == null || !socket.IsHandshakeComplete)
            {
                return Task.FromResult<BinaryNode>(null);
            }

            return socket.QueryGroupMetadataAsync(groupJid, cancellationToken);
        }

        public Task<BinaryNode> QueryParticipatingGroupsAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            IWhatsAppSocket socket = _sessions.Socket;
            if (socket == null || !socket.IsHandshakeComplete)
            {
                return Task.FromResult<BinaryNode>(null);
            }

            return socket.QueryParticipatingGroupsAsync(cancellationToken);
        }

        public async Task<GroupProfilePictureResult> GetPreviewPictureAsync(string jid)
        {
            IWhatsAppSocket socket = _sessions.Socket;
            if (socket == null || !socket.IsHandshakeComplete)
            {
                return new GroupProfilePictureResult
                {
                    FailureReason = "not-connected"
                };
            }

            ProfilePictureResult result = await socket.GetProfilePictureUrlResultAsync(jid, "preview")
                .ConfigureAwait(false);
            if (result == null)
            {
                return new GroupProfilePictureResult
                {
                    FailureReason = "empty-result"
                };
            }

            return new GroupProfilePictureResult
            {
                Url = result.Url,
                IsNotFound = result.IsNotFound,
                IsTimeout = result.IsTimeout,
                FailureReason = result.FailureReason
            };
        }

        public Task LeaveGroupAsync(string groupJid)
        {
            IWhatsAppSocket socket = _sessions.Socket;
            if (socket == null || !socket.IsHandshakeComplete)
            {
                return Task.FromException(
                    new InvalidOperationException("Not connected: leave group was not sent."));
            }

            return socket.LeaveGroupAsync(groupJid);
        }

        public Task<IReadOnlyList<CommunityLinkedGroupSnapshot>> FetchCommunityLinkedGroupsAsync(
            string communityOrGroupJid,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            IWhatsAppSocket socket = _sessions.Socket;
            if (socket == null || !socket.IsHandshakeComplete)
            {
                return Task.FromResult<IReadOnlyList<CommunityLinkedGroupSnapshot>>(
                    Array.Empty<CommunityLinkedGroupSnapshot>());
            }

            return socket.FetchCommunityLinkedGroupsAsync(communityOrGroupJid, cancellationToken);
        }

        public Task<bool> JoinGroupAsync(
            string groupJid,
            string inviteCode = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            IWhatsAppSocket socket = _sessions.Socket;
            if (socket == null || !socket.IsHandshakeComplete)
            {
                return Task.FromResult(false);
            }

            return socket.JoinGroupAsync(groupJid, inviteCode, cancellationToken);
        }

        public Task<string> CreateGroupAsync(string subject, IReadOnlyList<string> participantJids)
        {
            IWhatsAppSocket socket = RequireSocket("group create");
            return socket.CreateGroupAsync(subject, participantJids);
        }

        public Task<string> CreateCommunityAsync(string subject, string description)
        {
            IWhatsAppSocket socket = RequireSocket("community create");
            return socket.CreateCommunityAsync(subject, description);
        }

        public Task UpdateGroupEphemeralAsync(string groupJid, int seconds)
        {
            IWhatsAppSocket socket = RequireSocket("group ephemeral");
            return socket.UpdateGroupEphemeralAsync(groupJid, seconds);
        }

        public Task UpdateGroupPictureAsync(string groupJid, byte[] jpeg)
        {
            IWhatsAppSocket socket = RequireSocket("group picture");
            return socket.UpdateGroupPictureAsync(groupJid, jpeg);
        }

        public Task UpdateGroupLockedAsync(string groupJid, bool adminsOnly)
        {
            IWhatsAppSocket socket = RequireSocket("group locked");
            return socket.UpdateGroupLockedAsync(groupJid, adminsOnly);
        }

        public Task UpdateGroupAnnouncementAsync(string groupJid, bool adminsOnly)
        {
            IWhatsAppSocket socket = RequireSocket("group announcement");
            return socket.UpdateGroupAnnouncementAsync(groupJid, adminsOnly);
        }

        public Task UpdateGroupMemberAddModeAsync(string groupJid, bool adminsOnly)
        {
            IWhatsAppSocket socket = RequireSocket("group member-add");
            return socket.UpdateGroupMemberAddModeAsync(groupJid, adminsOnly);
        }

        public Task UpdateGroupJoinApprovalAsync(string groupJid, bool required)
        {
            IWhatsAppSocket socket = RequireSocket("group join-approval");
            return socket.UpdateGroupJoinApprovalAsync(groupJid, required);
        }

        private IWhatsAppSocket RequireSocket(string operation)
        {
            IWhatsAppSocket socket = _sessions.Socket;
            if (socket == null || !socket.IsHandshakeComplete)
            {
                throw new InvalidOperationException("Not connected: " + operation + " was not sent.");
            }

            return socket;
        }
    }
}
