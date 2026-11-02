using System;
using System.Collections.Generic;

namespace Unison.Core.Models
{
    /// <summary>What a <c>w:gp2</c> participants notification did.</summary>
    public enum GroupRosterEventKind
    {
        Add = 0,
        Remove = 1,
        Promote = 2,
        Demote = 3,

        /// <summary>The participant's address changed (typically phone → LID).</summary>
        Modify = 4
    }

    /// <summary>One participant named by a <see cref="GroupRosterEvent"/>.</summary>
    public sealed class GroupRosterEventParticipant
    {
        public GroupRosterEventParticipant(string jid, string phoneJid)
        {
            Jid = jid;
            PhoneJid = phoneJid;
        }

        public string Jid { get; }

        /// <summary>The PN companion when the row was LID-addressed; null otherwise.</summary>
        public string PhoneJid { get; }
    }

    /// <summary>A membership change for one group, as the host received it.</summary>
    public sealed class GroupRosterEvent
    {
        public GroupRosterEvent(
            string groupJid,
            GroupRosterEventKind kind,
            IReadOnlyList<GroupRosterEventParticipant> participants,
            DateTime atUtc)
        {
            GroupJid = groupJid;
            Kind = kind;
            Participants = participants ?? new GroupRosterEventParticipant[0];
            AtUtc = atUtc;
        }

        public string GroupJid { get; }

        public GroupRosterEventKind Kind { get; }

        public IReadOnlyList<GroupRosterEventParticipant> Participants { get; }

        public DateTime AtUtc { get; }
    }
}
