// =============================================================================
// GroupUpdate / GroupParticipantsUpdate
//
// What a w:gp2 notification turns into.
//
// These are split the way Baileys splits them: changes to the group itself go
// one way, changes to its membership another, because the app reacts to them
// differently - one edits a header, the other edits a list.
//
// Baileys also synthesises a stub WebMessageInfo for each of these so the change
// shows up as a system line inside the conversation. That half is deliberately
// not ported: the protobuf generated in this repo has no MessageStubType, so
// there is nothing faithful to build. When those descriptors are generated, the
// stub message belongs here, next to the event.
//
// Ports: rc14 groups.update and group-participants.update in src/Types/Events.ts
// =============================================================================
using System.Collections.Generic;

namespace Unison.Socket.Models
{
    /// <summary>What happened to the participants named in the update.</summary>
    public enum GroupParticipantAction
    {
        Add = 0,
        Remove = 1,
        Promote = 2,
        Demote = 3,

        /// <summary>The participant's addressing changed - typically a phone number moving to a LID.</summary>
        Modify = 4
    }

    /// <summary>A partial group: only what the notification actually changed.</summary>
    public sealed class GroupUpdate
    {
        public GroupUpdate()
        {
        }

        public GroupUpdate(string id)
        {
            Id = id;
        }

        public string Id { get; set; }

        /// <summary>Who made the change, when the server says.</summary>
        public string Author { get; set; }

        public string Subject { get; set; }

        public string SubjectOwner { get; set; }

        public long? SubjectTime { get; set; }

        public string Description { get; set; }

        public string DescriptionId { get; set; }

        /// <summary>True when only admins may send messages.</summary>
        public bool? Announce { get; set; }

        /// <summary>True when only admins may change the group's settings.</summary>
        public bool? Restrict { get; set; }

        /// <summary>True when ordinary members may add participants.</summary>
        public bool? MemberAddMode { get; set; }

        /// <summary>True when joining needs an admin's approval.</summary>
        public bool? JoinApprovalMode { get; set; }

        /// <summary>Disappearing-message duration in seconds; 0 turns it off.</summary>
        public int? EphemeralDuration { get; set; }

        public int? Size { get; set; }

        /// <summary>Set when the notification announced a brand new group rather than a change.</summary>
        public GroupMetadata Created { get; set; }
    }

    public sealed class GroupParticipantsUpdate
    {
        public GroupParticipantsUpdate()
        {
            Participants = new List<string>();
        }

        public string Id { get; set; }

        public string Author { get; set; }

        public GroupParticipantAction Action { get; set; }

        public IList<string> Participants { get; private set; }

        /// <summary>
        /// Per-participant failure codes, keyed by JID. The server reports a partial success rather
        /// than failing the whole request, so an add can leave some members out.
        /// </summary>
        public IDictionary<string, int> Reasons { get; set; }
    }
}
