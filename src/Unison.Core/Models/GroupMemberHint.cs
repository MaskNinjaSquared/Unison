using System;

namespace Unison.Core.Models
{
    /// <summary>Where a <see cref="GroupMemberHint"/> was observed.</summary>
    public enum GroupMemberHintSource
    {
        Message = 0,
        Quote = 1,
        Join = 2,
        RoleChange = 3,
        Metadata = 4,
        Store = 5,
        Viewport = 6
    }

    /// <summary>
    /// One thing the app learned about one participant: an author, a quote, a w:gp2 add/promote,
    /// a metadata row. Fields left null mean "not said", never "cleared".
    /// </summary>
    public sealed class GroupMemberHint
    {
        /// <summary>The participant as the source wrote it (LID or phone JID).</summary>
        public string Jid { get; set; }

        /// <summary>The other address when the source carried both (participant_alt / phone_number).</summary>
        public string AltJid { get; set; }

        public string PhoneNumber { get; set; }

        public string Lid { get; set; }

        public string DisplayName { get; set; }

        /// <summary>Null when the source says nothing about the role (a plain message).</summary>
        public GroupParticipantRole? Role { get; set; }

        public GroupMemberHintSource Source { get; set; }

        /// <summary>When the fact was true on the server (message / stanza / reply time), UTC.</summary>
        public DateTime ObservedAtUtc { get; set; }

        public static GroupMemberHint FromMember(GroupMember member, GroupMemberHintSource source, DateTime observedAtUtc)
        {
            if (member == null)
            {
                return null;
            }

            return new GroupMemberHint
            {
                Jid = member.Jid,
                PhoneNumber = member.PhoneNumber,
                Lid = member.Lid,
                DisplayName = member.DisplayName,
                Role = member.Role,
                Source = source,
                ObservedAtUtc = observedAtUtc
            };
        }
    }

    /// <summary>What <see cref="State.GroupRosterIndex"/> knows about one participant. Immutable.</summary>
    public sealed class GroupRosterRecord
    {
        public GroupRosterRecord(string jid, string lid, string phoneNumber, string displayName, GroupParticipantRole role)
        {
            Jid = jid;
            Lid = lid;
            PhoneNumber = phoneNumber;
            DisplayName = displayName;
            Role = role;
        }

        public string Jid { get; }

        public string Lid { get; }

        public string PhoneNumber { get; }

        public string DisplayName { get; }

        public GroupParticipantRole Role { get; }
    }
}
