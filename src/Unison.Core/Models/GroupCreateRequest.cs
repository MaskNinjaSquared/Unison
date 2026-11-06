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
    }

    public sealed class CommunityCreateRequest
    {
        public string Subject { get; set; }

        public string Description { get; set; }

        public byte[] PhotoBytes { get; set; }
    }
}
