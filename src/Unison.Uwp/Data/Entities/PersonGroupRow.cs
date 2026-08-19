using System;
using SQLite;

namespace Unison.Uwp.Data.Entities
{
    /// <summary>
    /// Person ↔ group membership (1:N). Inverse of <see cref="Unison.Core.Models.ChatItem.GroupMembers"/>.
    /// </summary>
    [Table("PersonGroup")]
    public sealed class PersonGroupRow
    {
        /// <summary>Composite key: personJid + "\u001f" + groupJid.</summary>
        [PrimaryKey]
        public string Id { get; set; }

        [Indexed(Name = "IX_PersonGroup_Person")]
        public string PersonJid { get; set; }

        [Indexed(Name = "IX_PersonGroup_Group")]
        public string GroupJid { get; set; }

        /// <summary><see cref="Unison.Core.Models.GroupParticipantRole"/> as INTEGER.</summary>
        public int Role { get; set; }

        public DateTime UpdatedAtUtc { get; set; }

        public static string MakeId(string personJid, string groupJid)
        {
            return (personJid ?? string.Empty) + "\u001f" + (groupJid ?? string.Empty);
        }
    }
}
