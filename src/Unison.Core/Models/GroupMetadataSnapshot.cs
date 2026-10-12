using System.Collections.Generic;

namespace Unison.Core.Models
{
    /// <summary>What a w:g2 listing or metadata reply says about one group, read off the wire.</summary>
    public sealed class GroupMetadataSnapshot
    {
        /// <summary>The group JID as the reply wrote it, completed with <c>@g.us</c>.</summary>
        public string Jid { get; set; }

        /// <summary>Null when the reply carried none, which a single-group query often does not.</summary>
        public string Subject { get; set; }

        public bool AnnounceOnly { get; set; }

        public GroupParticipantRole MyRole { get; set; }

        /// <summary>0 when the reply said nothing usable.</summary>
        public int MemberCount { get; set; }

        public List<GroupMemberDraft> Members { get; set; } = new List<GroupMemberDraft>();
    }

    /// <summary>A participant as the wire describes it, before names and avatars are attached.</summary>
    public sealed class GroupMemberDraft
    {
        public string Jid { get; set; }

        public string PhoneNumber { get; set; }

        public string Lid { get; set; }

        public GroupParticipantRole Role { get; set; }
    }
}
