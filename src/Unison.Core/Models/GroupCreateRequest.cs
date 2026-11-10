// =============================================================================
// GroupCreateRequest / CommunityCreateRequest
//
// What the New Group and New Community forms hand to IGroupService.
// =============================================================================
using System.Collections.Generic;

namespace Unison.Core.Models
{
    public sealed class GroupCreateRequest
    {
        public string Subject { get; set; }

        /// <summary>At least one member besides this account; the server refuses an empty list.</summary>
        public IReadOnlyList<string> ParticipantJids { get; set; }

        /// <summary>Picked image bytes (any format the platform decodes); null for no picture.</summary>
        public byte[] PhotoBytes { get; set; }

        public GroupEphemeralDuration Ephemeral { get; set; }

        /// <summary>Members may edit subject, picture, description, ephemeral, pin, and star.</summary>
        public bool MembersCanEditGroupSettings { get; set; } = true;

        /// <summary>Members may send messages (false = announcement / admins only).</summary>
        public bool MembersCanSendMessages { get; set; } = true;

        /// <summary>Members may add other members (false = admin_add).</summary>
        public bool MembersCanAddMembers { get; set; } = true;

        /// <summary>UI-only for now — invite link/QR has no create-time IQ flag.</summary>
        public bool MembersCanInviteViaLink { get; set; } = true;

        /// <summary>Admins must approve join-by-link requests.</summary>
        public bool AdminsMustApproveNewMembers { get; set; }
    }

    public sealed class CommunityCreateRequest
    {
        public string Subject { get; set; }

        public string Description { get; set; }

        public byte[] PhotoBytes { get; set; }
    }
}
