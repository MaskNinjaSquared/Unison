// =============================================================================
// GroupMetadata
//
// Everything the server says about a group, in the shape Baileys uses.
//
// Unison currently keeps a handful of these fields - subject, announce, my role
// - scattered over ChatItem and a few dictionaries, and drops the rest on the
// floor. Reading the whole node into one type costs nothing and means the parser
// stays a straight translation of the Baileys one, which is what makes it
// checkable against the reference when the protocol changes.
//
// Ports: rc14 src/Types/GroupMetadata.ts
// =============================================================================
using System.Collections.Generic;

namespace Unison.Socket.Models
{
    /// <summary>Which address space the group uses when routing messages between members.</summary>
    public enum GroupAddressingMode
    {
        /// <summary>
        /// The server did not say. This is not the same as pn: rc14 sends to a group whose mode
        /// it does not know as lid, and signing under the other identity produces a sender key
        /// the members do not look for.
        /// </summary>
        Unset = 0,

        Pn = 1,

        Lid = 2
    }

    /// <summary>The roles a participant can hold. Null admin in Baileys maps to <see cref="Member"/>.</summary>
    public enum GroupParticipantRole
    {
        Member = 0,
        Admin = 1,
        SuperAdmin = 2
    }

    public sealed class GroupParticipant
    {
        /// <summary>The JID the group addresses this member by, which may be a LID.</summary>
        public string Id { get; set; }

        /// <summary>Present when <see cref="Id"/> is a LID and the server disclosed the number.</summary>
        public string PhoneNumber { get; set; }

        /// <summary>Present when <see cref="Id"/> is a phone number and the server disclosed the LID.</summary>
        public string Lid { get; set; }

        public string Username { get; set; }

        public GroupParticipantRole Role { get; set; }

        public bool IsAdmin
        {
            get { return Role == GroupParticipantRole.Admin || Role == GroupParticipantRole.SuperAdmin; }
        }
    }

    public sealed class GroupMetadata
    {
        public GroupMetadata()
        {
            Participants = new List<GroupParticipant>();
            AddressingMode = GroupAddressingMode.Unset;
        }

        public string Id { get; set; }

        public string Notify { get; set; }

        public GroupAddressingMode AddressingMode { get; set; }

        public string Subject { get; set; }

        public string SubjectOwner { get; set; }

        public string SubjectOwnerPn { get; set; }

        public string SubjectOwnerUsername { get; set; }

        /// <summary>Unix seconds; 0 when the server did not say.</summary>
        public long SubjectTime { get; set; }

        public long Creation { get; set; }

        public string Owner { get; set; }

        public string OwnerPn { get; set; }

        public string OwnerUsername { get; set; }

        public string OwnerCountryCode { get; set; }

        public string Description { get; set; }

        public string DescriptionId { get; set; }

        public string DescriptionOwner { get; set; }

        public string DescriptionOwnerPn { get; set; }

        public string DescriptionOwnerUsername { get; set; }

        public long DescriptionTime { get; set; }

        /// <summary>The community this group belongs to, when it belongs to one.</summary>
        public string LinkedParent { get; set; }

        /// <summary>Only admins may change the group's settings.</summary>
        public bool Restrict { get; set; }

        /// <summary>Only admins may send messages.</summary>
        public bool Announce { get; set; }

        /// <summary>Members, not just admins, may add participants.</summary>
        public bool MemberAddMode { get; set; }

        /// <summary>Joining requires an admin's approval.</summary>
        public bool JoinApprovalMode { get; set; }

        public bool IsCommunity { get; set; }

        public bool IsCommunityAnnounce { get; set; }

        /// <summary>Participant count as reported by the server, which can exceed the listed participants.</summary>
        public int Size { get; set; }

        public IList<GroupParticipant> Participants { get; private set; }

        /// <summary>Disappearing-message duration in seconds, or null when the feature is off.</summary>
        public int? EphemeralDuration { get; set; }

        public string InviteCode { get; set; }
    }
}
