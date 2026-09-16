using System;
using SQLite;

namespace Unison.Uwp.Data.Entities
{
    /// <summary>
    /// One persisted group participant. Inverse of <see cref="PersonGroupRow"/> (group → members).
    /// </summary>
    [Table("GroupRosterMember")]
    public sealed class GroupRosterMemberRow
    {
        /// <summary>Composite: groupJid + "\u001f" + memberJid.</summary>
        [PrimaryKey]
        public string Id { get; set; }

        [Indexed(Name = "IX_GroupRoster_Group")]
        public string GroupJid { get; set; }

        [Indexed(Name = "IX_GroupRoster_Member")]
        public string MemberJid { get; set; }

        public string PhoneNumber { get; set; }

        public string Lid { get; set; }

        public string DisplayName { get; set; }

        /// <summary><see cref="Unison.Core.Models.GroupParticipantRole"/> as INTEGER.</summary>
        public int Role { get; set; }

        public string AvatarUrl { get; set; }

        public DateTime? AvatarFetchedAtUtc { get; set; }

        public DateTime? AvatarFetchFailedAtUtc { get; set; }

        public string AvatarFetchFailureReason { get; set; }

        public DateTime UpdatedAtUtc { get; set; }

        public static string MakeId(string groupJid, string memberJid)
        {
            return (groupJid ?? string.Empty) + "\u001f" + (memberJid ?? string.Empty);
        }
    }
}
