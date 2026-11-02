// =============================================================================
// IGroupWireActions
//
// Group IQ and picture lookup that today go through the UWP bridge socket.
// Portable façades call this port; the adapter forwards to IWhatsAppSocket.
// =============================================================================
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Abstractions
{
    /// <summary>Outcome of a group/member picture query (portable; no UWP Client types).</summary>
    public sealed class GroupProfilePictureResult
    {
        public string Url { get; set; }

        public bool IsNotFound { get; set; }

        public bool IsTimeout { get; set; }

        public string FailureReason { get; set; }
    }

    /// <summary>Wire-side group queries shared across linked devices.</summary>
    public interface IGroupWireActions
    {
        /// <summary>Null socket or handshake incomplete — IQ calls are not safe.</summary>
        bool IsHandshakeComplete { get; }

        Task<BinaryNode> QueryGroupMetadataAsync(
            string groupJid,
            CancellationToken cancellationToken = default(CancellationToken));

        Task<BinaryNode> QueryParticipatingGroupsAsync(
            CancellationToken cancellationToken = default(CancellationToken));

        Task<GroupProfilePictureResult> GetPreviewPictureAsync(string jid);

        /// <summary>Baileys <c>groupLeave</c> — leave as this account; not a kick.</summary>
        Task LeaveGroupAsync(string groupJid);

        /// <summary>Baileys <c>communityFetchLinkedGroups</c>.</summary>
        Task<IReadOnlyList<CommunityLinkedGroupSnapshot>> FetchCommunityLinkedGroupsAsync(
            string communityOrGroupJid,
            CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>Join a group by JID (community can-join) or invite code.</summary>
        Task<bool> JoinGroupAsync(
            string groupJid,
            string inviteCode = null,
            CancellationToken cancellationToken = default(CancellationToken));
    }
}
