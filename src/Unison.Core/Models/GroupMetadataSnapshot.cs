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

        /// <summary>
        /// Group description (<c>description/body</c>). Null when the reply carried no
        /// <c>description</c> node at all; empty when it carried one with no body.
        /// </summary>
        public string Description { get; set; }

        /// <summary>Wire <c>description/@id</c>. Null when the reply omitted it.</summary>
        public string DescriptionId { get; set; }

        public bool AnnounceOnly { get; set; }

        /// <summary>
        /// Community default announce channel (<c>default_sub_group</c> child on the group node).
        /// </summary>
        public bool IsCommunityAnnounce { get; set; }

        /// <summary>
        /// This node is a community parent (<c>parent</c> child on the group node).
        /// </summary>
        public bool IsCommunityParent { get; set; }

        /// <summary>
        /// Parent community JID when this group is linked (<c>linked_parent</c> attr).
        /// </summary>
        public string LinkedParentJid { get; set; }

        public GroupParticipantRole MyRole { get; set; }

        /// <summary>
        /// Null when the reply listed no participants (do not change the row).
        /// True when the signed-in user is on that list. False when participants
        /// were listed and the account is not among them — Baileys
        /// <c>chat.readOnly</c> after leave/remove.
        /// </summary>
        public bool? SelfIsParticipant { get; set; }

        /// <summary>0 when the reply said nothing usable.</summary>
        public int MemberCount { get; set; }

        /// <summary>Wire <c>creator</c> (often LID). Null when the reply omitted it.</summary>
        public string Owner { get; set; }

        /// <summary>Wire <c>creator_pn</c>. Null when the reply omitted it.</summary>
        public string OwnerPn { get; set; }

        /// <summary>Wire <c>creation</c> unix seconds. 0 when missing.</summary>
        public long CreationUnixSeconds { get; set; }

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
