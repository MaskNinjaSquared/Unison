using System;
using SQLite;

namespace Unison.Uwp.Data.Entities
{
    /// <summary>
    /// SQLite row for <see cref="Unison.Core.Models.Person"/> (persistence only).
    /// </summary>
    [Table("Person")]
    public sealed class PersonRow
    {
        [PrimaryKey]
        public string Jid { get; set; }

        public string Name { get; set; }

        public string AvatarUrl { get; set; }

        [Indexed(Name = "IX_Person_Phone")]
        public string Phone { get; set; }

        /// <summary><see cref="Unison.Core.Models.PersonSource"/> as INTEGER. 0 = Unknown.</summary>
        public int Source { get; set; }

        public DateTime UpdatedAtUtc { get; set; }
    }
}
